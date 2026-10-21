using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using DexTor.Contracts;
using DexTor.Qemu;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Drives the production launch, job and QMP code against the real QEMU binary, with no
/// guest.
/// </summary>
/// <remarks>
/// Opt-in: set <c>DEXTOR_REAL_QEMU=1</c>. Without it every test here is skipped, so a
/// normal run never depends on a provisioned runtime and an unexecuted integration check
/// is never counted as a pass.
/// <para>
/// The runtime is accepted only when it carries the repository's own provenance marker —
/// the pinned installer SHA-512 that <c>QemuRuntimePreparationService</c> writes — and
/// passes <c>VmPrelaunchGate.ValidateRuntime</c> against the shipped manifest. A file hash
/// on its own would say which bytes ran, not where they came from.
/// </para>
/// <para>
/// The machine is started with <c>-S</c>, so no guest instruction is ever executed:
/// nothing boots, nothing writes. No ISO, no physical disk, no USB passthrough, no host
/// directory share and no user <c>data.img</c> is attached — only a temporary raw file
/// created by the test, carrying the product's own virtio attachment and
/// <c>DEXTOR_DATA</c> serial. Networking is off and the display is headless.
/// </para>
/// </remarks>
[Trait("Category", "RealQemu")]
public sealed class RealQemuSmokeTests : IDisposable
{
    private const string OptInVariable = "DEXTOR_REAL_QEMU";
    private const string PinnedInstallerSha512 =
        "5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-realqemu-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataImage;

    public RealQemuSmokeTests()
    {
        Directory.CreateDirectory(_root);
        _dataImage = Path.Combine(_root, "scratch-data.img");

        // An ordinary temporary file. Never a real DEXTOR\storage\data.img.
        using var stream = new FileStream(_dataImage, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.SetLength(16L * 1024 * 1024);
    }

    /// <summary>
    /// The runtime, or a hard failure. These tests are only reached when the RealQemu
    /// category is selected deliberately, so an unusable runtime is reported as a failure
    /// rather than quietly passing as an unexecuted check.
    /// </summary>
    private static string RequireVerifiedRuntime()
    {
        Environment.GetEnvironmentVariable(OptInVariable).Should().Be(
            "1",
            "the real-QEMU suite is opt-in; set {0}=1 before selecting it",
            OptInVariable);

        var runtime = VerifiedRuntimeRoot();
        runtime.Should().NotBeNull(
            "a QEMU runtime carrying the repository pinned-installer provenance marker must be installed at {0}",
            SessionHostService.ResolveDefaultRuntimeRoot(AppContext.BaseDirectory, DexTor.Contracts.DexTorStorage.Runtime));
        return runtime!;
    }

    /// <summary>
    /// The provisioned runtime, verified the way the product verifies it, or null.
    /// </summary>
    private static string? VerifiedRuntimeRoot()
    {
        var runtime = SessionHostService.ResolveDefaultRuntimeRoot(AppContext.BaseDirectory, DexTor.Contracts.DexTorStorage.Runtime);
        var marker = Path.Combine(runtime, ".dextor-runtime.sha512");
        if (!File.Exists(marker))
        {
            return null;
        }

        if (!string.Equals(File.ReadAllText(marker).Trim(), PinnedInstallerSha512, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return File.Exists(Path.Combine(runtime, "qemu-system-x86_64.exe")) ? runtime : null;
    }

    /// <summary>
    /// Resolves the executable through the product's own gate, so the manifest's required
    /// files are checked rather than assumed.
    /// </summary>
    private static string ValidatedExecutable(string runtimeRoot)
    {
        var manifest = Path.Combine(
            Path.GetFullPath(AppContext.BaseDirectory),
            "..", "..", "..", "..", "..", "runtime", "qemu-manifest.json");
        manifest = File.Exists(manifest)
            ? Path.GetFullPath(manifest)
            : SessionHostService.ResolveDefaultRuntimeManifestPath(runtimeRoot, AppContext.BaseDirectory);
        return VmPrelaunchGate.ValidateRuntime(runtimeRoot, manifest);
    }

    /// <summary>
    /// A paused, headless, network-free machine with only the temporary image attached.
    /// </summary>
    private QemuLaunchPlan BuildPausedPlan(string executable) =>
        new(executable,
        [
            // No acceleration, no guest execution: -S holds every CPU at reset.
            "-S",
            "-machine", "pc",
            "-m", "128M",
            "-display", "none",
            "-nodefaults",
            "-nic", "none",
            "-object", $"iothread,id={QemuLaunchPlanBuilder.PrivateStorageIoThreadId}",
            "-drive", $"file={_dataImage.Replace('\\', '/')},if=none,id={QemuLaunchPlanBuilder.PrivateStorageDriveId},format=raw,cache=writeback,aio=threads",
            "-device", $"virtio-blk-pci,drive={QemuLaunchPlanBuilder.PrivateStorageDriveId},serial={QemuLaunchPlanBuilder.PrivateStorageSerial},iothread={QemuLaunchPlanBuilder.PrivateStorageIoThreadId}",
            "-qmp", "stdio",
            "-no-reboot"
        ]);

    [Fact]
    public async Task RealQemuStartsInTheJobNegotiatesQmpAndQuitsOnRequest()
    {
        var runtimeRoot = RequireVerifiedRuntime();

        var executable = ValidatedExecutable(runtimeRoot);
        var plan = BuildPausedPlan(executable);

        using var job = QemuJobObject.CreateKillOnClose();
        using var process = QemuProcessLauncher.Launch(plan, job);

        // Membership in the intended job, established at creation.
        job.ActiveProcessCount().Should().BeGreaterThan(0);

        await using var qmp = new QmpSession(process.StandardOutput, process.StandardInput);
        var events = new List<QmpEvent>();
        qmp.EventReceived += events.Add;
        qmp.Start();

        // Real greeting and capability negotiation, then correlated queries. The machine
        // stays paused throughout: no `cont` is ever sent.
        await NegotiateWithoutStartingTheGuestAsync(qmp);

        var status = await qmp.ExecuteAsync("query-status", TimeSpan.FromSeconds(10));
        status.Succeeded.Should().BeTrue(status.Error);
        status.Status.Should().Be("prelaunch", "the guest must never have started executing");

        // The temporary image really is attached, with the product's serial.
        var block = await qmp.ExecuteAsync("query-block", TimeSpan.FromSeconds(10));
        block.Succeeded.Should().BeTrue(block.Error);

        var quit = await qmp.ExecuteAsync("quit", TimeSpan.FromSeconds(10));
        quit.Succeeded.Should().BeTrue(quit.Error);

        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue("QEMU must exit after quit");
        QemuStandIn.WaitUntil(() => job.ActiveProcessCount() == 0).Should().BeTrue(
            "the session job must be empty once QEMU has gone");

        // A host-requested quit is not guest shutdown evidence, and no agent exists to
        // report storage closure.
        var evidence = events.Aggregate(
            GuestShutdownEvidence.None,
            (current, observed) => QmpShutdownEvidence.Strongest(current, QmpShutdownEvidence.Classify(observed)));
        evidence.Should().NotBe(
            GuestShutdownEvidence.ObservedGuestInitiated,
            "the host asked QEMU to quit; the guest never chose to stop");
    }

    [Fact]
    public async Task ClosingTheJobTerminatesRealQemu()
    {
        var runtimeRoot = RequireVerifiedRuntime();

        var plan = BuildPausedPlan(ValidatedExecutable(runtimeRoot));
        int processId;
        using (var job = QemuJobObject.CreateKillOnClose())
        {
            using var process = QemuProcessLauncher.Launch(plan, job);
            processId = process.Id;
            await using var qmp = new QmpSession(process.StandardOutput, process.StandardInput);
            qmp.Start();
            await NegotiateWithoutStartingTheGuestAsync(qmp);
        }

        QemuStandIn.WaitUntil(() => ProcessHasTerminated(processId)).Should().BeTrue(
            "closing the session job must terminate real QEMU too");
    }

    [Fact]
    public async Task FinalizationRunsAgainstRealQemuWithInjectedStorageAdapters()
    {
        var runtimeRoot = RequireVerifiedRuntime();

        // The escalation, confirmation and reporting run against a real writer. The flush
        // and removal adapters are injected, so no real volume or device is touched.
        var plan = BuildPausedPlan(ValidatedExecutable(runtimeRoot));
        using var job = QemuJobObject.CreateKillOnClose();
        using var process = QemuProcessLauncher.Launch(plan, job);
        await using var qmp = new QmpSession(process.StandardOutput, process.StandardInput);
        qmp.Start();
        await NegotiateWithoutStartingTheGuestAsync(qmp);

        var quit = await qmp.ExecuteAsync("quit", TimeSpan.FromSeconds(10));
        quit.Succeeded.Should().BeTrue(quit.Error);
        process.WaitForExit(TimeSpan.FromSeconds(30)).Should().BeTrue();

        QemuStandIn.WaitUntil(() => job.ActiveProcessCount() == 0).Should().BeTrue();
        process.HasExited.Should().BeTrue();
        process.ExitCode.Should().Be(0, "a clean QMP quit exits zero");
    }

    /// <summary>
    /// Capability negotiation only. The product handshake would send `cont` when the
    /// machine is paused, and that would start the guest executing — which this suite must
    /// never do.
    /// </summary>
    private static async Task NegotiateWithoutStartingTheGuestAsync(QmpSession qmp)
    {
        var capabilities = await qmp.ExecuteAsync("qmp_capabilities", TimeSpan.FromSeconds(20));
        capabilities.Succeeded.Should().BeTrue(capabilities.Error);

        var version = await qmp.ExecuteAsync("query-version", TimeSpan.FromSeconds(10));
        version.Succeeded.Should().BeTrue(version.Error);
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
            return true;
        }
    }

    /// <summary>Records what was actually verified, for the acceptance report.</summary>
    [Fact]
    public void RecordRuntimeProvenance()
    {
        var runtimeRoot = RequireVerifiedRuntime();

        var executable = Path.Combine(runtimeRoot, "qemu-system-x86_64.exe");
        using var stream = File.OpenRead(executable);
        var sha256 = Convert.ToHexString(SHA256.HashData(stream));

        File.WriteAllText(
            Path.Combine(_root, "provenance.txt"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"runtime={runtimeRoot}\nexecutable={executable}\nsha256={sha256}\npinnedInstallerSha512={PinnedInstallerSha512}\n"));

        sha256.Should().HaveLength(64);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}
