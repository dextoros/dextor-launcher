using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Contracts;
using DexTor.Qemu;
using DexTor.SessionHost;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Drives <see cref="SessionHostService"/> through the real start paths, launching a real
/// stand-in executable through the production launcher, and asserts that VM session
/// ownership is held for exactly as long as the process lives and is released on every
/// terminal path.
/// </summary>
public sealed class SessionOwnershipLifecycleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly string _usb;
    private readonly string _runtime;
    private readonly string _manifest;
    private readonly string _journalPath;
    private readonly SessionJournal _journal;
    private readonly string _dataImage;
    private readonly QemuStandIn _standIn;
    private const string RequestId = "aaaabbbb-cccc-dddd-eeee-ffff00001111";

    public SessionOwnershipLifecycleTests()
    {
        _usb = Path.Combine(_root, "usb");
        _runtime = Path.Combine(_root, "runtime");
        _journalPath = Path.Combine(_root, "session.json");
        _journal = new SessionJournal(_journalPath);
        _manifest = Path.Combine(_runtime, "qemu-manifest.json");

        var systemImage = CreateUsbFile(@"DEXTOR\system\slot-a\system.img", "dextor-system-image");
        _dataImage = CreateUsbFile(@"DEXTOR\storage\data.img", new string('0', 4096));

        Directory.CreateDirectory(Path.Combine(_usb, "DEXTOR", "metadata"));
        File.WriteAllText(
            Path.Combine(_usb, "DEXTOR", "metadata", "device.json"),
            JsonSerializer.Serialize(new
            {
                state = "ready",
                format = 3,
                requestId = RequestId,
                systemImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(systemImage))),
                systemImagePath = @"DEXTOR\system\slot-a\system.img",
                dataImagePath = @"DEXTOR\storage\data.img",
                storageState = "storage_not_initialized"
            }));

        // A real executable, started by the production launcher into the production job.
        _standIn = QemuStandIn.Install(_runtime, _manifest);
    }

    private DexTorSessionIdentity Identity => DexTorSessionIdentity.ForDataImage(_dataImage);

    /// <summary>The production path: nothing about starting QEMU is substituted.</summary>
    private SessionHostService CreateService() => new(null, _runtime, _manifest, _journalPath, null);

    private SessionHostService CreateService(
        Func<QemuLaunchPlan, QemuJobObject, CancellationToken, IQemuProcess> startProcess) =>
        new(null, _runtime, _manifest, _journalPath, startProcess);

    /// <summary>
    /// True when no process anywhere currently owns this device's session.
    /// </summary>
    /// <remarks>
    /// Probed on a thread of its own. Win32 mutex ownership is per-thread, so a probe that
    /// happened to run on the same thread that claimed the mutex would be let through by
    /// re-entrancy and would report a live claim as free. It also shows the ownership
    /// wrapper released through its own owning thread, because otherwise the kernel object
    /// would still be held when this probe runs.
    /// </remarks>
    private bool OwnershipIsFree()
    {
        var free = false;
        var probeThread = new Thread(() =>
        {
            var claim = DexTorSessionLock.TryAcquire(Identity);
            free = claim.IsGranted;
            claim.Lock?.Dispose();
        })
        {
            IsBackground = true
        };
        probeThread.Start();
        probeThread.Join();
        return free;
    }

    private JsonElement ReadJournal()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_journalPath));
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task ProcessStartFailureReleasesOwnership()
    {
        _standIn.BreakExecutable();
        await using var service = CreateService();

        var action = () => service.StartAsync(_usb);

        await action.Should().ThrowAsync<InvalidOperationException>();
        OwnershipIsFree().Should().BeTrue("a launch that never started QEMU must not keep the device claimed");
    }

    [Fact]
    public async Task AHandoverThatFailsAfterCreationContainsTheChildAndReleasesOwnership()
    {
        // The child is created by the production launcher and the handover then fails,
        // which is the window the job object exists for. Ownership may only come back once
        // that child is confirmed gone.
        await using var service = CreateService((plan, job, token) =>
        {
            using var created = QemuProcessLauncher.Launch(plan, job, token);
            throw new IOException("handover failed after the child was created");
        });

        var action = () => service.StartAsync(_usb);

        await action.Should().ThrowAsync<InvalidOperationException>();
        QemuStandIn.WaitUntil(() => !File.Exists(_standIn.ReportPath) || ProcessHasTerminated(_standIn.ReadProcessId()))
            .Should().BeTrue("a child that could not be handed over must not survive the failed start");
        OwnershipIsFree().Should().BeTrue();
    }

    [Fact]
    public async Task ProcessFactoryReturningNullReleasesOwnership()
    {
        await using var service = CreateService((_, _, _) => null!);

        var action = () => service.StartAsync(_usb);

        await action.Should().ThrowAsync<InvalidOperationException>();
        OwnershipIsFree().Should().BeTrue();
    }

    [Fact]
    public async Task StorageProbeFailureReleasesOwnership()
    {
        await using var service = CreateService();

        // Another handle holds data.img exclusively, so the launch cannot proceed.
        using (new FileStream(_dataImage, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var action = () => service.StartAsync(_usb);
            await action.Should().ThrowAsync<InvalidOperationException>();
        }

        OwnershipIsFree().Should().BeTrue("a failed storage probe must not strand the device");
    }

    [Fact]
    public async Task SecondLauncherForTheSameDeviceIsRejectedWhileASessionIsOwned()
    {
        using var firstLauncher = DexTorSessionLock.Acquire(Identity);
        await using var service = CreateService();

        var action = () => service.StartAsync(_usb);

        (await action.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*already running from this device*");
    }

    [Fact]
    public void ADifferentDeviceIsNotBlockedByAnActiveSession()
    {
        var otherImage = Path.Combine(_root, "other", "data.img");
        Directory.CreateDirectory(Path.GetDirectoryName(otherImage)!);
        File.WriteAllText(otherImage, "another device");
        using var otherDevice = DexTorSessionLock.Acquire(DexTorSessionIdentity.ForDataImage(otherImage));

        OwnershipIsFree().Should().BeTrue("ownership is scoped to one device, not to DexTor as a whole");
    }

    [Fact]
    public async Task MetadataWithoutARequestIdClaimsTheSameSessionAsMetadataWithOne()
    {
        // requestId correlates one provisioning run; it is optional in device.json and a
        // launcher that cannot read it must not end up on a different mutex for the same
        // data.img — that is a second writer, not a second device.
        RewriteMetadataWithoutRequestId();
        using var firstLauncher = DexTorSessionLock.Acquire(Identity);
        await using var service = CreateService();

        var action = () => service.StartAsync(_usb);

        (await action.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*already running from this device*");
    }

    [Fact]
    public async Task TheSameImageReachedThroughAnAliasIsTheSameSession()
    {
        // The USB is E:\ in one logon session and F:\ in another, and reachable through
        // junctions and mounted folders. Every alias must claim one session.
        var aliasRoot = Path.Combine(_root, "usb-alias");
        CreateJunction(aliasRoot, _usb);
        var aliasedImage = Path.Combine(aliasRoot, "DEXTOR", "storage", "data.img");

        DexTorSessionIdentity.ForDataImage(aliasedImage).Fingerprint
            .Should().Be(Identity.Fingerprint);

        using var firstLauncher = DexTorSessionLock.Acquire(
            DexTorSessionIdentity.ForDataImage(aliasedImage));
        await using var service = CreateService();

        var action = () => service.StartAsync(_usb);

        (await action.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*already running from this device*");
    }

    [Fact]
    public async Task AnAbandonedClaimDoesNotBypassTheExclusiveWriterProbe()
    {
        // A crashed predecessor leaves the mutex abandoned. That says the owner died, not
        // that its QEMU released data.img, so the writer probe must still run and still
        // refuse when something is holding the image.
        using var orphan = AbandonOwnership();
        await using var service = CreateService();

        // A writer that still permits readers: the image can be identified and ownership
        // can be taken over, so the start reaches the exclusive probe — which is exactly
        // where a surviving writer has to stop it.
        using (new FileStream(_dataImage, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var action = () => service.StartAsync(_usb);
            await action.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*cannot be locked exclusively*");
        }

        OwnershipIsFree().Should().BeTrue("the refused start must not keep the device claimed");
    }

    [Fact]
    public async Task AnAbandonedClaimIsReportedAndRecoveryComesFromTheJournalNotTheMutex()
    {
        // Two independent facts. The mutex was abandoned, which says a process died; the
        // journal says what that process had been doing. Recovery uses the second.
        _journal.Write(new SessionJournalEntry(Guid.NewGuid(), "running"));
        using var orphan = AbandonOwnership();
        var logger = new CapturingLogger();
        await using var service = new SessionHostService(logger, _runtime, _manifest, _journalPath, (_, _, _) => null!);

        var action = () => service.StartAsync(_usb);
        await action.Should().ThrowAsync<InvalidOperationException>();

        logger.EventIds.Should().Contain(
            1103,
            "taking over an abandoned mutex means the predecessor died, and that is worth recording");
        service.PreviousSession!.Disposition.Should().Be(
            PreviousSessionDisposition.Interrupted,
            "a session recorded as running and never closed out was interrupted");
        service.PreviousSession!.RequiresCaution.Should().BeTrue();
        ReadJournal().GetProperty("clean").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AMissingOrCorruptJournalIsTreatedAsInterruptedRatherThanClean()
    {
        File.WriteAllText(_journalPath, "{ this is not json");
        await using var service = CreateService();

        await service.StartAsync(_usb);

        service.PreviousSession!.Disposition.Should().Be(PreviousSessionDisposition.Unknown);
        service.PreviousSession!.RequiresCaution.Should().BeTrue(
            "\"cannot tell\" and \"was fine\" must not share an outcome");
        await service.StopAsync();
    }

    [Fact]
    public async Task AGoodStopIsHostFinalizedButNeverClaimedAsVerifiedCleanWithoutAnAgent()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
        report.GuestShutdown.Should().Be(GuestShutdownEvidence.ObservedGuestInitiated);
        report.HostFinalizationComplete.Should().BeTrue();

        // The comprehensive claim needs a guest agent, and there is none. Reporting it
        // anyway because QEMU exited is exactly the conflation Phase 6 exists to stop.
        report.GuestStorageClose.Should().Be(GuestStorageCloseEvidence.UnconfirmedNoAgent);
        report.GuestStorageVerifiedClean.Should().BeFalse();
        ReadJournal().GetProperty("clean").GetBoolean().Should().BeFalse();
        ReadJournal().GetProperty("hostFinalized").GetBoolean().Should().BeTrue();
        ReadJournal().GetProperty("state").GetString().Should().Be("stopped");
    }

    [Fact]
    public async Task AnUnexpectedExitIsRecordedWithoutShutdownEvidence()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);

        KillStandInFromOutside();

        (await WaitUntilAsync(() => service.State == SessionState.SafeToEject)).Should().BeTrue();
        ReadJournal().GetProperty("clean").GetBoolean().Should().BeFalse();
        ReadJournal().GetProperty("guestShutdown").GetString().Should().Be(
            nameof(GuestShutdownEvidence.ProcessExitOnly),
            "a process that vanished explained nothing about how the guest stopped");
    }

    [Fact]
    public async Task OwnershipIsHeldWhileQemuRunsAndReleasedAfterItExits()
    {
        await using var service = CreateService();

        await service.StartAsync(_usb);

        service.State.Should().Be(SessionState.Running);
        var processId = _standIn.ReadProcessId();
        ProcessHasTerminated(processId).Should().BeFalse();
        OwnershipIsFree().Should().BeFalse("the device stays claimed for the whole VM lifetime");

        // The stand-in honours the power button over QMP, as a real guest does.
        await service.StopAsync();

        ProcessHasTerminated(processId).Should().BeTrue();
        OwnershipIsFree().Should().BeTrue("ownership is released once QEMU has actually exited");
    }

    [Fact]
    public async Task OwnershipSurvivesUntilExitEvenWhenTheProcessDiesUnexpectedly()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);
        OwnershipIsFree().Should().BeFalse();

        KillStandInFromOutside();

        // The unexpected-exit path must free the device rather than leaving it claimed
        // until the launcher is closed.
        (await WaitUntilAsync(OwnershipIsFree)).Should().BeTrue();
    }

    [Fact]
    public async Task AFailedStartLeavesTheStorageImageByteForByteUnchanged()
    {
        var before = await File.ReadAllBytesAsync(_dataImage);
        var writeTimeBefore = File.GetLastWriteTimeUtc(_dataImage);
        _standIn.BreakExecutable();
        await using var service = CreateService();

        var action = () => service.StartAsync(_usb);
        await action.Should().ThrowAsync<InvalidOperationException>();

        var after = await File.ReadAllBytesAsync(_dataImage);
        after.Should().Equal(before);
        after.Should().HaveCount(4096);
        File.GetLastWriteTimeUtc(_dataImage).Should().Be(writeTimeBefore);
    }

    [Fact]
    public async Task ASuccessfulSessionLeavesTheStorageImageByteForByteUnchanged()
    {
        var before = await File.ReadAllBytesAsync(_dataImage);
        await using var service = CreateService();

        await service.StartAsync(_usb);
        await service.StopAsync();

        // The launcher only transports the image; nothing on the host writes to it.
        (await File.ReadAllBytesAsync(_dataImage)).Should().Equal(before);
    }

    /// <summary>
    /// Kills the stand-in the way an outside agent would: by its own process id, without
    /// going through the session host, so the service observes an unexpected exit.
    /// </summary>
    private void KillStandInFromOutside()
    {
        QemuStandIn.WaitUntil(() => File.Exists(_standIn.ReportPath)).Should().BeTrue();
        using var process = Process.GetProcessById(_standIn.ReadProcessId());
        process.Kill(entireProcessTree: true);
        process.WaitForExit(30_000).Should().BeTrue();
    }

    /// <summary>
    /// Leaves behind exactly what a crashed launcher does: a mutex whose owning thread
    /// died without releasing it. The returned handle keeps the kernel object alive — an
    /// object with no handles left is destroyed, and a recreated one is not abandoned.
    /// </summary>
    private Mutex AbandonOwnership()
    {
        Mutex? orphan = null;
        var name = Identity.MutexName;
        var crashed = new Thread(() =>
        {
            orphan = new Mutex(initiallyOwned: false, name);
            orphan.WaitOne(TimeSpan.Zero);
        });
        crashed.Start();
        crashed.Join();
        return orphan!;
    }

    private void RewriteMetadataWithoutRequestId()
    {
        var metadataPath = Path.Combine(_usb, "DEXTOR", "metadata", "device.json");
        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var root = document.RootElement;
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            state = root.GetProperty("state").GetString(),
            format = root.GetProperty("format").GetInt32(),
            systemImageSha256 = root.GetProperty("systemImageSha256").GetString(),
            systemImagePath = root.GetProperty("systemImagePath").GetString(),
            dataImagePath = root.GetProperty("dataImagePath").GetString(),
            storageState = root.GetProperty("storageState").GetString()
        }));
    }

    private static void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
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

    /// <summary>
    /// Reports on the calling thread, unlike <see cref="Progress{T}"/>, so a journal read
    /// inside the callback observes the state the service was in when it reported.
    /// </summary>
    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class CapturingLogger : ILogger<SessionHostService>
    {
        private readonly List<int> _eventIds = [];

        public IReadOnlyList<int> EventIds => _eventIds;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => _eventIds.Add(eventId.Id);
    }

    private static bool ProcessHasTerminated(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            // No such process: it exited and was reaped.
            return true;
        }
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return false;
    }

    private string CreateUsbFile(string relativePath, string content)
    {
        var path = Path.Combine(_usb, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A stand-in process may still hold the directory; the temp folder is disposable.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
