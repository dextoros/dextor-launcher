using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace DexTor.Qemu.Tests;

public sealed class DexTorSessionLockTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dextor-lock-" + Guid.NewGuid().ToString("N"));

    public DexTorSessionLockTests() => Directory.CreateDirectory(_root);

    /// <summary>A real file, because storage identity is a property of the file, not the string.</summary>
    private string CreateImage(string relativePath = "data.img")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "persistent storage stand-in");
        return path;
    }

    private DexTorSessionIdentity UniqueIdentity() =>
        DexTorSessionIdentity.ForDataImage(CreateImage(Guid.NewGuid().ToString("N") + "/data.img"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A junction or a still-open handle; the temp folder is disposable.
        }
    }

    // ---------------------------------------------------------------- identity

    [Fact]
    public void IdentityIsDeviceSpecificRatherThanAConstant()
    {
        var first = DexTorSessionIdentity.ForDataImage(CreateImage(@"a\data.img"));
        var second = DexTorSessionIdentity.ForDataImage(CreateImage(@"b\data.img"));

        first.Fingerprint.Should().NotBe(second.Fingerprint);
        first.MutexName.Should().NotBe(second.MutexName);
    }

    [Fact]
    public void SupportedAliasesOfOneImageResolveToOneOwnershipIdentity()
    {
        var real = CreateImage(@"device\DEXTOR\storage\data.img");
        var expected = DexTorSessionIdentity.ForDataImage(real);

        // A junction is the alias a path-string comparison cannot see through: it has a
        // different relative path on the same volume, yet names the same file.
        var junction = Path.Combine(_root, "aliased-device");
        CreateJunction(junction, Path.Combine(_root, "device"));

        var aliases = new[]
        {
            Path.Combine(junction, "DEXTOR", "storage", "data.img"),
            @"\\?\" + real,
            real.ToUpperInvariant(),
            real.ToLowerInvariant(),
            Path.Combine(_root, "device", "DEXTOR", "system", "..", "storage", "data.img")
        };

        foreach (var alias in aliases)
        {
            DexTorSessionIdentity.ForDataImage(alias).Fingerprint.Should().Be(
                expected.Fingerprint,
                "{0} names the same data.img and must not license a second writer",
                alias);
        }
    }

    [Fact]
    public void AnImageThatCannotBeIdentifiedIsRejectedRatherThanKeyedByItsPathString()
    {
        // Fail closed: without a canonical identity there is no evidence that some other
        // spelling of this image is not already claimed.
        var action = () => DexTorSessionIdentity.ForDataImage(Path.Combine(_root, "not-created", "data.img"));

        action.Should().Throw<VmPrelaunchException>().WithMessage("*missing*");
    }

    [Fact]
    public void AnImageReachedThroughANetworkPathIsRejected()
    {
        var action = () => DexTorSessionIdentity.ForDataImage(@"\\127.0.0.1\nonexistent-share$\DEXTOR\storage\data.img");

        action.Should().Throw<VmPrelaunchException>();
    }

    [Fact]
    public void AMultiplyLinkedImageIsRejectedBeforeStartup()
    {
        // NTFS is the supported layout for the DexTor volume and NTFS has hard links. A
        // final path is the name a handle was opened by, so two hard links to one file
        // normalize to two different volume-GUID paths: normalization alone cannot tell
        // "two names for one file" from "two files". Both names must therefore be refused
        // rather than silently keyed to different mutexes.
        var real = CreateImage(@"linked\data.img");
        var link = Path.Combine(_root, "linked", "data-second-name.img");
        CreateHardLink(link, real);

        // Precondition: the two names really are one file, and normalization does report
        // them differently — which is exactly why the rejection is needed.
        File.WriteAllText(real, "shared bytes");
        File.ReadAllText(link).Should().Be("shared bytes");

        foreach (var name in new[] { real, link })
        {
            var action = () => DexTorSessionIdentity.ForDataImage(name);
            action.Should().Throw<VmPrelaunchException>("{0} is one of two names for one image", name)
                .WithMessage("*more than one name*");
        }
    }

    [Fact]
    public void RemovingTheExtraLinkMakesTheImageUsableAgain()
    {
        var real = CreateImage(@"relinked\data.img");
        var link = Path.Combine(_root, "relinked", "data-second-name.img");
        CreateHardLink(link, real);
        var blocked = () => DexTorSessionIdentity.ForDataImage(real);
        blocked.Should().Throw<VmPrelaunchException>();

        File.Delete(link);

        // The rejection tracks the actual link count, so it is a condition the operator
        // can clear rather than a permanent verdict on the device.
        DexTorSessionIdentity.ForDataImage(real).Fingerprint.Should().HaveLength(32);
    }

    [Fact]
    public void ProvisioningMetadataDoesNotParticipateInTheOwnershipIdentity()
    {
        // The identity is derived from the storage alone. A launcher that can read the
        // device's requestId and one that cannot must still claim the same mutex, which
        // is only true when the requestId is not part of the key.
        var image = CreateImage();
        var canonical = WindowsVolumeStorageCanonicalizer.Default.Canonicalize(image);

        DexTorSessionIdentity.ForDataImage(image).Fingerprint
            .Should().Be(DexTorSessionIdentity.ForCanonicalStorage(canonical).Fingerprint);
    }

    [Fact]
    public void LockNameLeaksNeitherHostPathNorDeviceMetadata()
    {
        var identity = DexTorSessionIdentity.ForDataImage(CreateImage(@"someone\DEXTOR\storage\data.img"));

        foreach (var name in new[] { identity.MutexName, identity.Fingerprint })
        {
            name.Should().NotContain("someone");
            name.Should().NotContain("DEXTOR");
            name.Should().NotContain("data.img");
            name.Should().NotContain("Volume{");
        }

        // A one-way fingerprint, not an encoding of the inputs.
        identity.Fingerprint.Should().HaveLength(32);
        identity.Fingerprint.Should().MatchRegex("^[0-9A-F]{32}$");
    }

    // ------------------------------------------------------- ownership domain

    [Fact]
    public void OwnershipIsAlwaysClaimedMachineWide()
    {
        var identity = DexTorSessionIdentity.ForDataImage(CreateImage());

        // One namespace for every launcher on the machine. A per-logon-session name
        // would let two Windows accounts each believe they own the same USB.
        identity.MutexName.Should().StartWith(@"Global\");

        // Exactly one separator, immediately after the namespace prefix: more would make
        // the name an illegal kernel object path.
        identity.MutexName.Count(character => character == '\\').Should().Be(1);
        identity.MutexName.Length.Should().BeLessThan(260);
    }

    [Fact]
    public void AnUnusableGlobalObjectFailsClosedInsteadOfDowngradingTheScope()
    {
        var identity = DexTorSessionIdentity.ForDataImage(CreateImage());

        // A different kernel object type under the same name makes the mutex
        // uncreatable. Before, this was read as "the global namespace is unavailable"
        // and answered by claiming a Local\ mutex no other launcher would look at.
        using var collision = new Semaphore(1, 1, identity.MutexName);

        var claim = DexTorSessionLock.TryAcquire(identity);

        claim.IsGranted.Should().BeFalse();
        claim.Outcome.Should().Be(SessionOwnershipOutcome.Unavailable);
        var action = () => DexTorSessionLock.Acquire(identity);
        action.Should().Throw<VmPrelaunchException>()
            .WithMessage("*machine-wide ownership*")
            .WithMessage("*startup was stopped*");
    }

    [Fact]
    public void RefusalsDistinguishContentionFromAccessDenialAndFromAnUnusableNamespace()
    {
        var contention = DexTorSessionLock.DescribeRefusal(
            new SessionOwnershipClaim(SessionOwnershipOutcome.HeldByAnotherProcess, null, null));
        var denial = DexTorSessionLock.DescribeRefusal(
            new SessionOwnershipClaim(SessionOwnershipOutcome.DeniedToThisAccount, null, "Access is denied."));
        var unavailable = DexTorSessionLock.DescribeRefusal(
            new SessionOwnershipClaim(SessionOwnershipOutcome.Unavailable, null, "a diagnostic"));

        contention.Should().Be("DexTor is already running from this device.");
        denial.Should().Contain("another Windows account");
        unavailable.Should().Contain("a diagnostic").And.Contain("startup was stopped");
        new[] { contention, denial, unavailable }.Should().OnlyHaveUniqueItems();
    }

    // --------------------------------------------------------------- claiming

    [Fact]
    public void FirstSessionAcquiresOwnership()
    {
        var identity = UniqueIdentity();

        var claim = DexTorSessionLock.TryAcquire(identity);
        using var held = claim.Lock!;

        claim.Outcome.Should().Be(SessionOwnershipOutcome.Acquired);
        held.IsHeld.Should().BeTrue();
        held.Identity.Should().Be(identity);
        held.RecoveredFromAbandonedSession.Should().BeFalse();
    }

    [Fact]
    public void SecondSessionForTheSameDeviceIsRejected()
    {
        var identity = UniqueIdentity();
        using var first = DexTorSessionLock.Acquire(identity);

        var second = DexTorSessionLock.TryAcquire(identity);

        second.IsGranted.Should().BeFalse();
        second.Outcome.Should().Be(SessionOwnershipOutcome.HeldByThisProcess);
    }

    [Fact]
    public void AClaimHeldElsewhereOnTheMachineIsReportedAsContention()
    {
        // Mutex ownership belongs to a thread, so a live foreign thread holding the raw
        // object is indistinguishable from a second launcher process holding it.
        var identity = UniqueIdentity();
        using var foreignOwnerRelease = new ManualResetEventSlim(false);
        using var foreignOwnerClaimed = new ManualResetEventSlim(false);
        var foreignOwner = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, identity.MutexName);
            mutex.WaitOne(TimeSpan.Zero);
            foreignOwnerClaimed.Set();
            foreignOwnerRelease.Wait();
            mutex.ReleaseMutex();
        })
        {
            IsBackground = true
        };
        foreignOwner.Start();
        foreignOwnerClaimed.Wait();

        try
        {
            var claim = DexTorSessionLock.TryAcquire(identity);

            claim.IsGranted.Should().BeFalse();
            claim.Outcome.Should().Be(SessionOwnershipOutcome.HeldByAnotherProcess);
            var action = () => DexTorSessionLock.Acquire(identity);
            action.Should().Throw<VmPrelaunchException>()
                .WithMessage("DexTor is already running from this device.");
        }
        finally
        {
            foreignOwnerRelease.Set();
            foreignOwner.Join();
        }
    }

    [Fact]
    public void ADifferentProcessSeesTheClaimThroughTheGlobalNamespace()
    {
        var identity = UniqueIdentity();
        using var held = DexTorSessionLock.Acquire(identity);

        // A real second process, not a mock: cross-process exclusion is the whole point,
        // and only another process can demonstrate it.
        ProbeMutexFromAnotherProcess(identity.MutexName).Should().Be(
            "held",
            "another process must see the machine-wide claim");
    }

    [Fact]
    public void DifferentDevicesDoNotConflict()
    {
        using var first = DexTorSessionLock.Acquire(UniqueIdentity());
        using var second = DexTorSessionLock.Acquire(UniqueIdentity());

        first.IsHeld.Should().BeTrue();
        second.IsHeld.Should().BeTrue();
        first.Identity.Fingerprint.Should().NotBe(second.Identity.Fingerprint);
    }

    [Fact]
    public void ReleasingOwnershipLetsTheNextSessionStart()
    {
        var identity = UniqueIdentity();
        var first = DexTorSessionLock.Acquire(identity);
        first.Dispose();

        first.IsHeld.Should().BeFalse();
        using var second = DexTorSessionLock.Acquire(identity);
        second.IsHeld.Should().BeTrue();
    }

    [Fact]
    public void ReleasingOwnershipTwiceIsSafe()
    {
        var sessionLock = DexTorSessionLock.Acquire(UniqueIdentity());

        sessionLock.Dispose();
        var action = sessionLock.Dispose;

        action.Should().NotThrow();
    }

    [Fact]
    public void ASecondAcquireInsideTheSameProcessIsRejected()
    {
        // A Windows mutex is re-entrant for its owning thread, so this would silently
        // succeed if ownership relied on the kernel object alone.
        var identity = UniqueIdentity();
        using var first = DexTorSessionLock.Acquire(identity);

        var second = DexTorSessionLock.TryAcquire(identity);

        second.IsGranted.Should().BeFalse();
        first.IsHeld.Should().BeTrue("the rejected attempt must not disturb the live claim");
    }

    [Fact]
    public void ARefusedClaimDoesNotConsumeTheProcessWideReservation()
    {
        var identity = UniqueIdentity();
        using (var collision = new Semaphore(1, 1, identity.MutexName))
        {
            DexTorSessionLock.TryAcquire(identity).IsGranted.Should().BeFalse();
        }

        // The failed attempt must leave no stale in-process registry entry behind, or the
        // device would stay unclaimable for the rest of the launcher's life.
        using var afterwards = DexTorSessionLock.Acquire(identity);
        afterwards.IsHeld.Should().BeTrue();
    }

    // -------------------------------------------------------- thread affinity

    [Fact]
    public async Task CleanupFromADifferentThreadStillReleasesOwnership()
    {
        var identity = UniqueIdentity();
        var sessionLock = await Task.Run(() => DexTorSessionLock.Acquire(identity));

        // Releasing happens on process-exit callbacks, cancellations and async
        // continuations, none of which run on the thread that claimed the mutex. A
        // Win32 mutex can only be released by its owning thread, so a wrapper that calls
        // ReleaseMutex inline would fail here and leave the object owned.
        var cleanupThread = new Thread(sessionLock.Dispose) { IsBackground = true };
        cleanupThread.Start();
        cleanupThread.Join();

        sessionLock.IsHeld.Should().BeFalse();

        // Proved on a third thread: had the release silently failed, the kernel object
        // would still be owned by the live ownership thread and this would time out.
        ClaimRawMutexOnAFreshThread(identity.MutexName).Should().BeTrue(
            "the kernel object must actually be free, not merely marked released in-process");
    }

    [Fact]
    public async Task OwnershipClaimedOnOneThreadIsVisibleAndReleasableFromAnother()
    {
        var identity = UniqueIdentity();
        var sessionLock = await Task.Run(() => DexTorSessionLock.Acquire(identity));

        try
        {
            ClaimRawMutexOnAFreshThread(identity.MutexName).Should().BeFalse(
                "the claim must hold regardless of which thread made it");
        }
        finally
        {
            await Task.Run(sessionLock.Dispose);
        }

        using var successor = DexTorSessionLock.Acquire(identity);
        successor.IsHeld.Should().BeTrue();
    }

    // ------------------------------------------------------------ abandonment

    [Fact]
    public void AbandonedOwnershipIsRecoveredRatherThanStrandingTheDevice()
    {
        var identity = UniqueIdentity();

        // Mutex ownership is per-thread. A thread that claims the raw mutex and exits
        // without releasing leaves behind exactly what a crashed launcher process does,
        // including an in-process registry that has no record of the claim.
        Mutex? orphan = null;
        var crashed = new Thread(() =>
        {
            orphan = new Mutex(initiallyOwned: false, identity.MutexName);
            orphan.WaitOne(TimeSpan.Zero);
        });
        crashed.Start();
        crashed.Join();

        var claim = DexTorSessionLock.TryAcquire(identity);
        using var recovered = claim.Lock!;

        claim.Outcome.Should().Be(
            SessionOwnershipOutcome.RecoveredFromAbandonedSession,
            "a crashed launcher must not permanently strand the device as already running");

        // Abandonment is evidence that the predecessor died, never that it shut down
        // cleanly. The flag exists so callers keep running their writer and recovery
        // checks, not so they can skip them.
        recovered.RecoveredFromAbandonedSession.Should().BeTrue();
        recovered.IsHeld.Should().BeTrue();
        orphan!.Dispose();
    }

    [Fact]
    public void RecoveringAnAbandonedClaimStillReleasesCleanly()
    {
        var identity = UniqueIdentity();
        Mutex? orphan = null;
        var crashed = new Thread(() =>
        {
            orphan = new Mutex(initiallyOwned: false, identity.MutexName);
            orphan.WaitOne(TimeSpan.Zero);
        });
        crashed.Start();
        crashed.Join();

        // The orphan handle stays open until the successor has claimed: a kernel object
        // with no handles left is destroyed, and a recreated mutex is not abandoned.
        var recovered = DexTorSessionLock.Acquire(identity);
        orphan!.Dispose();
        recovered.RecoveredFromAbandonedSession.Should().BeTrue();

        // Ownership taken over from an abandoned mutex must be released the same way a
        // normal claim is, including from a foreign thread.
        var cleanupThread = new Thread(recovered.Dispose) { IsBackground = true };
        cleanupThread.Start();
        cleanupThread.Join();

        ClaimRawMutexOnAFreshThread(identity.MutexName).Should().BeTrue();
    }

    // ----------------------------------------------------------------- probes

    /// <summary>
    /// Tries to take the raw kernel object on a thread that has never touched it, so
    /// Win32 mutex re-entrancy cannot mask a failed release.
    /// </summary>
    private static bool ClaimRawMutexOnAFreshThread(string name)
    {
        var claimed = false;
        var probe = new Thread(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, name);
            try
            {
                claimed = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                claimed = true;
            }

            if (claimed)
            {
                mutex.ReleaseMutex();
            }
        })
        {
            IsBackground = true
        };
        probe.Start();
        probe.Join(TimeSpan.FromSeconds(10)).Should().BeTrue("the probe thread must not block");
        return claimed;
    }

    /// <summary>
    /// Reports "held" or "free" from a genuinely separate process, which is the only way
    /// to demonstrate cross-process exclusion.
    /// </summary>
    private static string ProbeMutexFromAnotherProcess(string name)
    {
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "$m = New-Object System.Threading.Mutex($false, '" + name + "'); "
            + "if ($m.WaitOne(0)) { $m.ReleaseMutex(); 'free' } else { 'held' }");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit(30_000).Should().BeTrue();
        return output;
    }

    /// <summary>
    /// A real NTFS hard link. <c>mklink /H</c> needs no elevation, and the temp volume
    /// used here is NTFS, which is also the supported layout for the DexTor volume.
    /// </summary>
    private static void CreateHardLink(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/H");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(
            0,
            "a hard link needs no elevation on an NTFS volume: {0}",
            error);
    }

    private static void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(link);
        startInfo.ArgumentList.Add(target);

        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        process.ExitCode.Should().Be(0, "a directory junction needs no elevation: {0}", error);
    }
}
