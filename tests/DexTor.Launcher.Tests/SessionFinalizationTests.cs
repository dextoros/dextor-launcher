using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Contracts;
using DexTor.Qemu;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Phase 6 end to end: one finalization operation per session, bounded escalation,
/// confirmed termination, and the outcomes kept apart from one another.
/// </summary>
/// <remarks>
/// Every session here is a real Windows process started through the production launcher
/// into the production job. Only the host adapters — volume flush and device removal —
/// are injected, so no test can reach a real disk.
/// </remarks>
public sealed class SessionFinalizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-final-" + Guid.NewGuid().ToString("N"));
    private readonly string _usb;
    private readonly string _runtime;
    private readonly string _manifest;
    private readonly string _journalPath;
    private readonly string _dataImage;
    private readonly QemuStandIn _standIn;

    /// <summary>Short stages, so escalation is exercised without waiting out real timers.</summary>
    private static readonly ShutdownPolicy FastPolicy = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2));

    public SessionFinalizationTests()
    {
        _usb = Path.Combine(_root, "usb");
        _runtime = Path.Combine(_root, "runtime");
        _journalPath = Path.Combine(_root, "session.json");
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
                requestId = "aaaabbbb-cccc-dddd-eeee-ffff00001111",
                systemImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(systemImage))),
                systemImagePath = @"DEXTOR\system\slot-a\system.img",
                dataImagePath = @"DEXTOR\storage\data.img",
                storageState = "storage_not_initialized"
            }));

        _standIn = QemuStandIn.Install(_runtime, _manifest);
    }

    /// <summary>
    /// The same policy, with room for a stand-in that has to be scheduled to exit.
    /// </summary>
    /// <remarks>
    /// <see cref="FastPolicy"/>'s two-second guest budget is what keeps the escalation
    /// tests quick: they configure a stand-in that ignores the power button, and the
    /// timeout elapsing is the thing under test. A test that expects the guest to exit
    /// gracefully must not share that budget — under a loaded machine, a real child
    /// process can take longer than two seconds to be scheduled and exit, and the run then
    /// escalates for a reason that has nothing to do with the product. The wait ends when
    /// the process exits, so a longer bound costs nothing when the machine is idle.
    /// </remarks>
    private static readonly ShutdownPolicy PatientGuestPolicy = FastPolicy with
    {
        GuestShutdownTimeout = TimeSpan.FromSeconds(30)
    };

    private SessionHostService CreateService(
        IHostVolumeFlusher? flusher = null,
        IRemovableDeviceResolver? resolver = null,
        IDeviceRemover? remover = null,
        Func<QemuLaunchPlan, QemuJobObject, CancellationToken, IQemuProcess>? startProcess = null,
        ShutdownPolicy? policy = null) =>
        new(null,
            _runtime,
            _manifest,
            _journalPath,
            startProcess,
            policy ?? FastPolicy,
            flusher ?? new FakeVolumeFlusher(HostFlushResult.Succeeded),
            resolver ?? FakeDeviceResolver.Resolving(),
            remover ?? new FakeDeviceRemover(DeviceRemovalResult.Removed));

    private JsonElement ReadJournal()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(_journalPath));
        return document.RootElement.Clone();
    }

    private bool OwnershipIsFree()
    {
        var free = false;
        var probe = new Thread(() =>
        {
            var claim = DexTorSessionLock.TryAcquire(DexTorSessionIdentity.ForDataImage(_dataImage));
            free = claim.IsGranted;
            claim.Lock?.Dispose();
        })
        {
            IsBackground = true
        };
        probe.Start();
        probe.Join();
        return free;
    }

    // ------------------------------------------------------------- shutdown evidence

    [Fact]
    public async Task AGuestThatShutsItselfDownIsObservedWhenTheEventFollowsTheAcknowledgement()
    {
        // This asserts that the shutdown stopped at the power button, so it must not fail
        // merely because a busy machine took longer than the escalation budget to schedule
        // the stand-in's exit.
        await using var service = CreateService(policy: PatientGuestPolicy);
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.GuestShutdown.Should().Be(GuestShutdownEvidence.ObservedGuestInitiated);
        report.Escalation.Should().Be(ShutdownEscalation.PowerButton);
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
    }

    [Fact]
    public async Task AGuestThatShutsItselfDownIsObservedWhenTheEventPrecedesTheAcknowledgement()
    {
        // QEMU may report the event before it answers the command. Ordering must not
        // decide whether the evidence is seen.
        _standIn.Configure(emitShutdownEventBeforeAck: true);
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.GuestShutdown.Should().Be(GuestShutdownEvidence.ObservedGuestInitiated);
    }

    [Fact]
    public async Task AFinalEventImmediatelyFollowedByProcessExitIsStillObserved()
    {
        // No delay between the event and the process dying: the bytes are in the pipe
        // while the process object is already signalling.
        _standIn.Configure(exitDelayMilliseconds: 0);
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.GuestShutdown.Should().Be(GuestShutdownEvidence.ObservedGuestInitiated);
    }

    [Theory]
    [InlineData("GUEST_PANICKED", true, "guest-shutdown", GuestShutdownEvidence.ObservedPanic)]
    [InlineData("SHUTDOWN", true, "guest-reset", GuestShutdownEvidence.ObservedHostInitiated)]
    [InlineData("SHUTDOWN", false, "host-qmp-quit", GuestShutdownEvidence.ObservedHostInitiated)]
    [InlineData("SHUTDOWN", true, "some-future-reason", GuestShutdownEvidence.ObservedHostInitiated)]
    public async Task APanicResetOrUnknownCauseIsNotAcceptedAsAnOrderlyShutdown(
        string eventName,
        bool guest,
        string reason,
        GuestShutdownEvidence expected)
    {
        _standIn.Configure(
            shutdownEventName: eventName,
            shutdownEventGuest: guest,
            shutdownEventReason: reason);
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.GuestShutdown.Should().Be(expected);
        report.GuestStorageVerifiedClean.Should().BeFalse();
    }

    [Fact]
    public async Task AnExitWithNoShutdownEventEstablishesNothingAboutTheGuest()
    {
        _standIn.Configure(emitShutdownEvent: false);
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.GuestShutdown.Should().Be(GuestShutdownEvidence.ProcessExitOnly);
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
    }

    // ------------------------------------------------------------------- escalation

    [Fact]
    public async Task AnIgnoredPowerdownEscalatesToQuit()
    {
        _standIn.Configure(honorPowerdown: false, honorQuit: true);
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.Escalation.Should().Be(ShutdownEscalation.Quit);
        report.WasForced.Should().BeTrue();
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
    }

    [Fact]
    public async Task AGuestThatIgnoresEverythingIsTerminatedThroughTheJob()
    {
        _standIn.Configure(honorPowerdown: false, honorQuit: false);
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        report.Escalation.Should().Be(ShutdownEscalation.JobTerminated);
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
        report.WasForced.Should().BeTrue();
    }

    [Fact]
    public async Task ADescendantThatOutlivesQemuKeepsTerminationUnconfirmedUntilTheJobIsEmpty()
    {
        // The writer set is the job, not the one process. A descendant still running means
        // the session's writers are not gone, whatever the parent handle says.
        _standIn.Configure(spawnDescendant: true, honorPowerdown: true);
        await using var service = CreateService();
        await service.StartAsync(_usb);
        QemuStandIn.WaitUntil(() => File.Exists(_standIn.DescendantPidPath)).Should().BeTrue();
        var descendant = _standIn.ReadDescendantProcessId();

        var report = await service.StopAsync();

        report.Escalation.Should().Be(
            ShutdownEscalation.JobTerminated,
            "the descendant forced the terminal stage even though QEMU itself had exited");
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
        QemuStandIn.WaitUntil(() => ProcessHasTerminated(descendant)).Should().BeTrue();
    }

    [Fact]
    public async Task AStopSkipsStagesTheWriterHasAlreadySatisfied()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);
        KillStandInFromOutside();
        (await WaitUntilAsync(() => service.State == SessionState.SafeToEject)).Should().BeTrue();

        var started = Stopwatch.StartNew();
        var report = await service.StopAsync();
        started.Stop();

        // Nothing is left to ask, so no stage timer is waited out.
        started.Elapsed.Should().BeLessThan(FastPolicy.GuestShutdownTimeout);
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
    }

    // ------------------------------------------- unconfirmed termination is protective

    [Fact]
    public async Task AnUnconfirmedTerminationFlushesNothingEjectsNothingAndKeepsOwnership()
    {
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded);
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(
            flusher,
            remover: remover,
            startProcess: (plan, job, token) => new StubbornProcess(QemuProcessLauncher.Launch(plan, job, token)));
        await service.StartAsync(_usb);

        var report = await service.StopAndEjectAsync();

        report.WriterTermination.Should().Be(WriterTerminationResult.Unconfirmed);
        flusher.Calls.Should().Be(0, "a writer that may still be running must not be flushed around");
        remover.Calls.Should().Be(0, "a device with a possible live writer must not be ejected");
        report.DeviceRemoval.Should().Be(DeviceRemovalResult.Skipped);
        service.State.Should().NotBe(SessionState.SafeToEject);
        OwnershipIsFree().Should().BeFalse("containment is kept while a writer may remain");
        ReadJournal().GetProperty("state").GetString().Should().Be("termination-unconfirmed");
    }

    // ----------------------------------------------------- one operation, many callers

    [Fact]
    public async Task ConcurrentStopRequestsJoinOneOperation()
    {
        _standIn.Configure(honorPowerdown: false, honorQuit: false);
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded);
        await using var service = CreateService(flusher);
        await service.StartAsync(_usb);

        var reports = await Task.WhenAll(
            service.StopAsync(),
            service.StopAsync(),
            service.StopAsync());

        reports.Should().AllSatisfy(report =>
            report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed));

        // One run means one flush, whatever the number of callers.
        flusher.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AStopAndAnEjectRequestedTogetherRunAsOneSequence()
    {
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(remover: remover);
        await service.StartAsync(_usb);

        var stop = service.StopAsync();
        var stopAndEject = service.StopAndEjectAsync();
        await Task.WhenAll(stop, stopAndEject);

        // Joining with an eject upgrades the run rather than starting a competing one.
        remover.Calls.Should().Be(1);
        (await stopAndEject).CanRemoveUsb.Should().BeTrue();
    }

    [Fact]
    public async Task CallerCancellationAfterAcceptanceStopsTheWaitButNotTheShutdown()
    {
        _standIn.Configure(honorPowerdown: false, honorQuit: false);
        await using var service = CreateService();
        await service.StartAsync(_usb);
        var processId = _standIn.ReadProcessId();

        using var cancellation = new CancellationTokenSource();
        var waiting = service.StopAsync(cancellationToken: cancellation.Token);
        await cancellation.CancelAsync();
        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The caller walked away; the session still had to be finished.
        QemuStandIn.WaitUntil(() => ProcessHasTerminated(processId)).Should().BeTrue(
            "abandoning the wait must not abandon the shutdown");
        var report = await service.StopAsync();
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
    }

    [Fact]
    public async Task ARequestCancelledBeforeAcceptanceStartsNothing()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var act = async () => await service.StopAsync(cancellationToken: cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        service.State.Should().Be(SessionState.Running, "a request never accepted changed nothing");
        await service.StopAsync();
    }

    [Fact]
    public async Task ASpontaneousExitAndAStopRequestDoNotFinalizeTwice()
    {
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded);
        await using var service = CreateService(flusher);
        await service.StartAsync(_usb);

        // The stand-in may die on its own while the operator is asking it to stop. Which
        // of the two got there first is exactly the race being tested, so the kill is
        // best-effort and the assertion is about how many runs happened.
        var stop = service.StopAsync();
        TryKillStandInFromOutside();
        await stop;

        (await WaitUntilAsync(() => service.State == SessionState.SafeToEject)).Should().BeTrue();
        flusher.Calls.Should().Be(1, "the exit callback joins the run, it does not start another");
    }

    // ------------------------------------------------------------------ flush outcomes

    [Theory]
    [InlineData(HostFlushResult.Denied)]
    [InlineData(HostFlushResult.Failed)]
    [InlineData(HostFlushResult.Unavailable)]
    public async Task AFlushThatDidNotSucceedIsReportedAsItselfAndStillAllowsARemovalRequest(HostFlushResult outcome)
    {
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(
            new FakeVolumeFlusher(outcome, "detail"),
            remover: remover);
        await service.StartAsync(_usb);

        var report = await service.StopAndEjectAsync();

        report.Flush.Should().Be(outcome);
        report.FlushDetail.Should().Be("detail");

        // A denied flush is a separate fact from whether Windows will release the device.
        // Neither is rewritten as the other.
        remover.Calls.Should().Be(1);
        report.CanRemoveUsb.Should().BeTrue();
    }

    [Fact]
    public async Task AFlushStillRunningKeepsOwnershipUntilItReturns()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded) { Gate = gate };
        await using var service = CreateService(flusher);
        await service.StartAsync(_usb);

        var stopping = service.StopAsync();
        QemuStandIn.WaitUntil(() => flusher.Calls == 1).Should().BeTrue();

        // The native call has not returned. Ownership is not handed back on a guess.
        OwnershipIsFree().Should().BeFalse();
        service.State.Should().NotBe(SessionState.SafeToEject);

        gate.SetResult();
        var report = await stopping;
        report.Flush.Should().Be(HostFlushResult.Succeeded);
        OwnershipIsFree().Should().BeTrue();
    }

    [Fact]
    public async Task TheFlushTargetsTheImageVolumeAndRunsAfterOurHandlesAreClosed()
    {
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded);
        await using var service = CreateService(flusher);
        await service.StartAsync(_usb);

        await service.StopAsync();

        flusher.LastPath.Should().Be(_dataImage);
    }

    // --------------------------------------------------------------- removal outcomes

    [Fact]
    public async Task OnlyASuccessfulRemovalSaysTheUsbCanBeRemoved()
    {
        await using var service = CreateService(remover: new FakeDeviceRemover(DeviceRemovalResult.Removed));
        await service.StartAsync(_usb);

        var report = await service.StopAndEjectAsync();

        report.CanRemoveUsb.Should().BeTrue();
    }

    [Theory]
    [InlineData(DeviceRemovalResult.Vetoed)]
    [InlineData(DeviceRemovalResult.Denied)]
    [InlineData(DeviceRemovalResult.Failed)]
    public async Task ARejectedRemovalStaysRejected(DeviceRemovalResult outcome)
    {
        await using var service = CreateService(remover: new FakeDeviceRemover(outcome, "veto type 5"));
        await service.StartAsync(_usb);

        var report = await service.StopAndEjectAsync();

        report.DeviceRemoval.Should().Be(outcome);
        report.DeviceRemovalDetail.Should().Be("veto type 5");
        report.CanRemoveUsb.Should().BeFalse();

        // Nothing is forced in response, and the session is still finished properly.
        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
        OwnershipIsFree().Should().BeTrue("a settled removal failure leaves no writer to protect");
    }

    [Fact]
    public async Task AnUnresolvableOrWrongTargetIsNeverGuessedAt()
    {
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(
            resolver: FakeDeviceResolver.Refusing("The USB volume spans more than one disk."),
            remover: remover);
        await service.StartAsync(_usb);

        var report = await service.StopAndEjectAsync();

        remover.Calls.Should().Be(0, "an unresolved target must not be replaced with a nearby one");
        report.DeviceRemoval.Should().Be(DeviceRemovalResult.TargetUnresolved);
        report.DeviceRemovalDetail.Should().Contain("more than one disk");
    }

    [Fact]
    public async Task EveryUsbHandleIsClosedBeforeRemovalIsRequested()
    {
        var closed = false;
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed)
        {
            // Observed at the moment of the call: an exclusive open of data.img can only
            // succeed if nothing of ours still holds it.
            ObserveOnCall = () => closed = CanOpenExclusively(_dataImage)
        };
        await using var service = CreateService(remover: remover);
        await service.StartAsync(_usb);

        await service.StopAndEjectAsync();

        remover.ObservationAtCall.Should().BeTrue();
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task StoppingNeverRequestsRemoval()
    {
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(remover: remover);
        await service.StartAsync(_usb);

        var report = await service.StopAsync();

        remover.Calls.Should().Be(0);
        report.DeviceRemoval.Should().Be(DeviceRemovalResult.NotRequested);
        report.CanRemoveUsb.Should().BeFalse();
    }

    [Fact]
    public async Task AStandaloneEjectRevalidatesInsteadOfTrustingAnEarlierStop()
    {
        var resolver = FakeDeviceResolver.Resolving();
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(resolver: resolver, remover: remover);
        await service.StartAsync(_usb);
        await service.StopAsync();
        var resolvesAfterStop = resolver.Calls;

        var report = await service.EjectAsync(_usb);

        resolver.Calls.Should().BeGreaterThan(resolvesAfterStop, "the device is resolved again, not remembered");
        remover.Calls.Should().Be(1);
        report.CanRemoveUsb.Should().BeTrue();
        OwnershipIsFree().Should().BeTrue("the standalone eject releases what it took");
    }

    [Fact]
    public async Task AStandaloneEjectTakesOwnershipForTheAttempt()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);
        await service.StopAsync();

        // Someone else holds the device, so the eject must refuse rather than act on it.
        using var other = DexTorSessionLock.Acquire(DexTorSessionIdentity.ForDataImage(_dataImage));
        var report = await service.EjectAsync(_usb);

        report.DeviceRemoval.Should().Be(DeviceRemovalResult.Skipped);
        report.CanRemoveUsb.Should().BeFalse();
    }

    // ---------------------------------------------------------- forced outcomes stick

    [Fact]
    public async Task ASuccessfulRemovalDoesNotEraseAForcedStopOrUnverifiedStorage()
    {
        _standIn.Configure(honorPowerdown: false, honorQuit: false);
        await using var service = CreateService(remover: new FakeDeviceRemover(DeviceRemovalResult.Removed));
        await service.StartAsync(_usb);

        var report = await service.StopAndEjectAsync();

        report.CanRemoveUsb.Should().BeTrue();
        report.WasForced.Should().BeTrue("a later success does not change how the guest stopped");
        report.Escalation.Should().Be(ShutdownEscalation.JobTerminated);
        report.GuestStorageVerifiedClean.Should().BeFalse();
        ReadJournal().GetProperty("forced").GetBoolean().Should().BeTrue();
        ReadJournal().GetProperty("clean").GetBoolean().Should().BeFalse();
    }

    // ------------------------------------------------------------- journal ordering

    [Fact]
    public async Task TheDirtyRecordIsDurableBeforeTheWriterExists()
    {
        string? stateAtCreation = null;
        await using var service = CreateService(startProcess: (plan, job, token) =>
        {
            // Read at the moment the child is about to be created.
            stateAtCreation = ReadJournal().GetProperty("state").GetString();
            return QemuProcessLauncher.Launch(plan, job, token);
        });

        await service.StartAsync(_usb);

        stateAtCreation.Should().Be("running", "the session is on record before anything can write to the image");
        await service.StopAsync();
    }

    [Fact]
    public async Task AStartIsRefusedWhenTheDirtyRecordCannotBePersisted()
    {
        // A directory where the journal file should be: the write cannot succeed.
        Directory.CreateDirectory(_journalPath);
        var started = false;
        await using var service = CreateService(startProcess: (plan, job, token) =>
        {
            started = true;
            return QemuProcessLauncher.Launch(plan, job, token);
        });

        var act = () => service.StartAsync(_usb);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*could not record the session state durably*");
        started.Should().BeFalse("no writer may exist without a record of it");
        OwnershipIsFree().Should().BeTrue();
    }

    [Fact]
    public async Task AJournalFailureDuringFinalizationIsReportedNotSwallowed()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);

        // The journal becomes unwritable after the session started.
        File.Delete(_journalPath);
        Directory.CreateDirectory(_journalPath);

        var report = await service.StopAsync();

        report.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
        report.Journal.Should().Be(JournalPersistenceResult.Failed);
        report.HostFinalizationComplete.Should().BeFalse(
            "an outcome that never reached the disk is not a completed finalization");
    }

    [Fact]
    public async Task NoProductionPathClaimsVerifiedCleanStorage()
    {
        await using var service = CreateService();
        await service.StartAsync(_usb);

        var report = await service.StopAndEjectAsync();

        report.GuestStorageClose.Should().Be(GuestStorageCloseEvidence.UnconfirmedNoAgent);
        report.GuestStorageVerifiedClean.Should().BeFalse();
        ReadJournal().GetProperty("guestStorageClose").GetString()
            .Should().Be(nameof(GuestStorageCloseEvidence.UnconfirmedNoAgent));
    }

    // ------------------------------------------------------------------------ helpers

    private static bool CanOpenExclusively(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void TryKillStandInFromOutside()
    {
        try
        {
            KillStandInFromOutside();
        }
        catch (ArgumentException)
        {
            // It had already gone; that is one of the orderings under test.
        }
    }

    private void KillStandInFromOutside()
    {
        QemuStandIn.WaitUntil(() => File.Exists(_standIn.ReportPath)).Should().BeTrue();
        using var process = Process.GetProcessById(_standIn.ReadProcessId());
        process.Kill(entireProcessTree: true);
        process.WaitForExit(30_000).Should().BeTrue();
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

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(25);
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

    /// <summary>
    /// A process wrapper that never admits to having exited, standing in for a writer
    /// whose termination cannot be confirmed.
    /// </summary>
    private sealed class StubbornProcess(IQemuProcess inner) : IQemuProcess
    {
        public int Id => inner.Id;

        public bool HasExited => false;

        public int ExitCode => 0;

        public StreamWriter StandardInput => inner.StandardInput;

        public StreamReader StandardOutput => inner.StandardOutput;

        public StreamReader StandardError => inner.StandardError;

        public event EventHandler? Exited
        {
            add => inner.Exited += value;
            remove => inner.Exited -= value;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.Infinite, cancellationToken);

        public bool WaitForExit(TimeSpan timeout) => false;

        public void Kill() => inner.Kill();

        public void Dispose() => inner.Dispose();
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
