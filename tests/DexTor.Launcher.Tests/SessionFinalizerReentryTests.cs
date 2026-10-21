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
/// What happens when finalization is asked for again: after it finished, while it is
/// running, and after an attempt that could not finish the job.
/// </summary>
/// <remarks>
/// Real Windows processes through the production launcher; only the flush and removal
/// adapters are injected, so nothing here can reach a real device. Barriers are
/// deterministic — a gated adapter, a stand-in that refuses to stop — never sleeps.
/// </remarks>
public sealed class SessionFinalizerReentryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-reentry-" + Guid.NewGuid().ToString("N"));
    private readonly string _usb;
    private readonly string _runtime;
    private readonly string _manifest;
    private readonly string _journalPath;
    private readonly string _dataImage;
    private readonly QemuStandIn _standIn;

    private static readonly ShutdownPolicy FastPolicy = new(
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(2));

    public SessionFinalizerReentryTests()
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

    private SessionHostService CreateService(
        IHostVolumeFlusher? flusher = null,
        IRemovableDeviceResolver? resolver = null,
        IDeviceRemover? remover = null,
        Func<QemuLaunchPlan, QemuJobObject, CancellationToken, IQemuProcess>? startProcess = null) =>
        new(null,
            _runtime,
            _manifest,
            _journalPath,
            startProcess,
            FastPolicy,
            flusher ?? new FakeVolumeFlusher(HostFlushResult.Succeeded),
            resolver ?? FakeDeviceResolver.Resolving(),
            remover ?? new FakeDeviceRemover(DeviceRemovalResult.Removed));

    // ---------------------------------------------------------------------------- A

    [Fact]
    public async Task AStandaloneEjectAfterAReleasedStopReacquiresOwnershipAndRevalidates()
    {
        var resolver = FakeDeviceResolver.Resolving();
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed)
        {
            // Ownership must be held again at the moment the request is issued.
            ObserveOnCall = () => !OwnershipIsFree()
        };
        await using var service = CreateService(resolver: resolver, remover: remover);
        await service.StartAsync(_usb);

        await service.StopAsync();
        OwnershipIsFree().Should().BeTrue("a completed stop hands the device back");
        var resolvesAfterStop = resolver.Calls;

        var report = await service.EjectAsync(_usb);

        resolver.Calls.Should().BeGreaterThan(resolvesAfterStop, "the device is resolved again, not remembered");
        remover.ObservationAtCall.Should().BeTrue("the eject holds ownership for its own attempt");
        report.CanRemoveUsb.Should().BeTrue();
        OwnershipIsFree().Should().BeTrue("and gives it back once the attempt has settled");
    }

    // ---------------------------------------------------------------------------- B

    [Fact]
    public async Task AVetoedRemovalCanBeRetriedAndTheOldVetoIsNotReturnedForever()
    {
        var remover = new RetryingRemover(DeviceRemovalResult.Vetoed, DeviceRemovalResult.Removed);
        await using var service = CreateService(remover: remover);
        await service.StartAsync(_usb);

        var first = await service.StopAndEjectAsync();
        first.DeviceRemoval.Should().Be(DeviceRemovalResult.Vetoed);
        first.CanRemoveUsb.Should().BeFalse();

        // Whatever was holding the device has let go. The retry must reach Windows again
        // rather than replay the completed first attempt.
        var second = await service.EjectAsync(_usb);

        remover.Calls.Should().Be(2);
        second.DeviceRemoval.Should().Be(DeviceRemovalResult.Removed);
        second.CanRemoveUsb.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------- C

    [Fact]
    public async Task AnUnconfirmedTerminationCanBeFinalizedOnceTheProcessActuallyExits()
    {
        // The first attempt cannot confirm the writer is gone, so it keeps containment.
        // Once the process does exit, a later request must be able to finish the job
        // instead of being handed the stale "unconfirmed" result for ever.
        var stubborn = new ControllableProcess();
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded);
        await using var service = CreateService(
            flusher,
            startProcess: (plan, job, token) => stubborn.Wrap(QemuProcessLauncher.Launch(plan, job, token)));
        await service.StartAsync(_usb);

        var first = await service.StopAsync();
        first.WriterTermination.Should().Be(WriterTerminationResult.Unconfirmed);
        OwnershipIsFree().Should().BeFalse("an unresolved writer keeps the device");
        flusher.Calls.Should().Be(0);

        // The process really has gone now.
        stubborn.AdmitExit();
        var second = await service.StopAsync();

        second.WriterTermination.Should().Be(WriterTerminationResult.Confirmed);
        flusher.Calls.Should().Be(1, "the second attempt ran the work the first could not");
        OwnershipIsFree().Should().BeTrue("ownership is not leaked once termination is confirmed");
    }

    // ---------------------------------------------------------------------------- D

    [Fact]
    public async Task AnEjectAskedForAfterTheStopCompletedIsStillCarriedOut()
    {
        // The stop finished and released the session before the eject arrived. The intent
        // must become a subsequent owned operation, not be silently dropped.
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(remover: remover);
        await service.StartAsync(_usb);
        await service.StopAsync();

        var report = await service.StopAndEjectAsync();

        remover.Calls.Should().Be(1, "the eject intent survived the completion boundary");
        report.DeviceRemoval.Should().Be(DeviceRemovalResult.Removed);
        report.CanRemoveUsb.Should().BeTrue();
    }

    [Fact]
    public async Task AnEjectJoiningARunInProgressIsCarriedOutExactlyOnce()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded) { Gate = gate };
        var remover = new FakeDeviceRemover(DeviceRemovalResult.Removed);
        await using var service = CreateService(flusher, remover: remover);
        await service.StartAsync(_usb);

        var stopping = service.StopAsync();
        QemuStandIn.WaitUntil(() => flusher.Calls == 1).Should().BeTrue();

        // Arrives while the run is inside the flush, before it reaches the removal step.
        var ejecting = service.StopAndEjectAsync();
        gate.SetResult();
        await Task.WhenAll(stopping, ejecting);

        remover.Calls.Should().Be(1);
        (await ejecting).CanRemoveUsb.Should().BeTrue();
    }

    // ---------------------------------------------------------------------------- E

    [Fact]
    public async Task ACallerThatStopsWaitingLeavesTheNativeCallAloneAndKeepsOwnership()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flusher = new FakeVolumeFlusher(HostFlushResult.Succeeded) { Gate = gate };
        await using var service = CreateService(flusher);
        await service.StartAsync(_usb);

        using var cancellation = new CancellationTokenSource();
        var waiting = service.StopAsync(cancellationToken: cancellation.Token);
        QemuStandIn.WaitUntil(() => flusher.Calls == 1).Should().BeTrue("the flush is in flight");

        await cancellation.CancelAsync();
        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The caller walked away. Nothing about the native call changed: it was not
        // reissued, the device was not handed back, and the state is not a success.
        flusher.Calls.Should().Be(1, "no second native operation was started");
        OwnershipIsFree().Should().BeFalse("ownership is not released while a native call is outstanding");
        service.State.Should().NotBe(SessionState.SafeToEject);

        gate.SetResult();
        var report = await service.StopAsync();
        report.Flush.Should().Be(HostFlushResult.Succeeded);
        flusher.Calls.Should().Be(1, "the completed run was joined, not repeated");
        OwnershipIsFree().Should().BeTrue();
    }

    // ------------------------------------------------------------------------ helpers

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

    private string CreateUsbFile(string relativePath, string content)
    {
        var path = Path.Combine(_usb, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Vetoes the first request and accepts the next.</summary>
    private sealed class RetryingRemover(DeviceRemovalResult first, DeviceRemovalResult then) : IDeviceRemover
    {
        public int Calls { get; private set; }

        public Task<(DeviceRemovalResult Result, string? Detail)> RequestRemovalAsync(
            RemovableTarget target,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Calls == 1 ? (first, "veto type 5") : (then, (string?)null));
        }
    }

    /// <summary>
    /// A process that withholds its exit until the test says otherwise, so "termination
    /// could not be confirmed" is reached deterministically rather than by timing.
    /// </summary>
    private sealed class ControllableProcess
    {
        private volatile bool _admitExit;

        public void AdmitExit() => _admitExit = true;

        public IQemuProcess Wrap(IQemuProcess inner) => new Wrapper(inner, () => _admitExit);

        private sealed class Wrapper(IQemuProcess inner, Func<bool> admitted) : IQemuProcess
        {
            public int Id => inner.Id;

            public bool HasExited => admitted() && inner.HasExited;

            public int ExitCode => admitted() ? inner.ExitCode : 0;

            public StreamWriter StandardInput => inner.StandardInput;

            public StreamReader StandardOutput => inner.StandardOutput;

            public StreamReader StandardError => inner.StandardError;

            public event EventHandler? Exited
            {
                add => inner.Exited += value;
                remove => inner.Exited -= value;
            }

            public Task WaitForExitAsync(CancellationToken cancellationToken) =>
                admitted() ? inner.WaitForExitAsync(cancellationToken) : Task.Delay(Timeout.Infinite, cancellationToken);

            public bool WaitForExit(TimeSpan timeout) => admitted() && inner.WaitForExit(timeout);

            public void Kill() => inner.Kill();

            public void Dispose() => inner.Dispose();
        }
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
