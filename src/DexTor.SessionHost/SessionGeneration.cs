using DexTor.Contracts;
using DexTor.Qemu;

namespace DexTor.SessionHost;

/// <summary>
/// Everything that belongs to one run of one VM: the writer, its job, the ownership
/// claim, the QMP session and the finalization that ends them.
/// </summary>
/// <remarks>
/// Generations exist so a callback can tell which session it belongs to. QEMU's exit
/// notification, a QMP event buffered near process exit and a UI request can all arrive
/// after the session they refer to is finished and a new one has started. Each captures
/// its generation and acts only if it is still the current one, so an old session can
/// never mark a new one stopped, release its ownership or publish its state.
/// </remarks>
internal sealed class SessionGeneration(
    Guid sessionId,
    string dataImagePath,
    string? volumeGuidPath,
    SessionJournal journal,
    DexTorSessionLock sessionLock,
    QemuJobObject job)
{
    private readonly List<IDisposable> _usbHandles = [];
    private readonly object _gate = new();
    private int _handlesClosed;

    public Guid SessionId => sessionId;

    public string DataImagePath => dataImagePath;

    /// <summary>
    /// The volume identity captured when this session started.
    /// </summary>
    /// <remarks>
    /// Finalization resolves the device from this, not from a fresh look at whatever the
    /// path points to now. A drive letter can be reassigned between starting and stopping,
    /// and the flush and the removal have to act on the storage this session was actually
    /// holding.
    /// </remarks>
    public string? VolumeGuidPath => volumeGuidPath;

    public SessionJournal Journal => journal;

    public IQemuProcess? Process { get; set; }

    public QmpSession? Qmp { get; set; }

    public SessionFinalizer? Finalizer { get; set; }

    /// <summary>The strongest guest-shutdown evidence this session has observed.</summary>
    public GuestShutdownEvidence ObservedShutdown { get; private set; } = GuestShutdownEvidence.None;

    /// <summary>The last storage error QEMU reported, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Raised for each finalization stage, for progress reporting.</summary>
    public Action<SessionFinalizationStage>? StageObserver { get; set; }

    public void Report(SessionFinalizationStage stage) => StageObserver?.Invoke(stage);

    /// <summary>
    /// Folds in one observed QMP event. Only ever strengthens the recorded evidence.
    /// </summary>
    public void Observe(QmpEvent qmpEvent)
    {
        var classified = QmpShutdownEvidence.Classify(qmpEvent);
        lock (_gate)
        {
            ObservedShutdown = QmpShutdownEvidence.Strongest(ObservedShutdown, classified);
        }
    }

    /// <summary>Marks that QEMU exited without any event having explained it.</summary>
    public void ObserveProcessExit()
    {
        lock (_gate)
        {
            if (ObservedShutdown == GuestShutdownEvidence.None)
            {
                ObservedShutdown = GuestShutdownEvidence.ProcessExitOnly;
            }
        }
    }

    /// <summary>Registers a handle on the USB that must be closed before removal.</summary>
    public void TrackUsbHandle(IDisposable handle)
    {
        lock (_gate)
        {
            _usbHandles.Add(handle);
        }
    }

    /// <summary>
    /// Closes everything this session holds open on the USB. Idempotent, and always run
    /// before a flush or a removal request.
    /// </summary>
    public void CloseUsbHandles()
    {
        IDisposable[] handles;
        lock (_gate)
        {
            handles = [.. _usbHandles];
            _usbHandles.Clear();
        }

        foreach (var handle in handles)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
            }
        }

        Interlocked.Exchange(ref _handlesClosed, 1);
    }

    /// <summary>True once <see cref="CloseUsbHandles"/> has run.</summary>
    public bool UsbHandlesClosed => Volatile.Read(ref _handlesClosed) == 1;

    /// <summary>
    /// Claims the right to tear this generation down, once.
    /// </summary>
    /// <remarks>
    /// Several callers join one finalization run and all receive its report, so all of
    /// them arrive here. Only the first may dispose the streams and release containment;
    /// the rest simply return the outcome they were given.
    /// </remarks>
    public bool TryBeginSettle() => Interlocked.Exchange(ref _settled, 1) == 0;

    private int _settled;

    /// <summary>How many processes the session job still holds.</summary>
    public uint RemainingJobMembers() => job.ActiveProcessCount();

    /// <summary>
    /// The job's member count once it has had a moment to empty after QEMU exited.
    /// </summary>
    /// <remarks>
    /// QEMU is a console program started without a window, so it still gets a
    /// <c>conhost.exe</c>, and that console host is a member of the job too. It leaves a
    /// moment after QEMU does - longer on a busy machine. Read at the instant QEMU's
    /// handle was signalled, the count was often one, and a clean guest shutdown was
    /// escalated to terminating the job and reported as a forced stop. Returns as soon
    /// as the job is empty; only what is still there after <paramref name="within"/> is
    /// a process that outlived QEMU.
    /// </remarks>
    public Task<uint> WaitForJobToDrainAsync(TimeSpan within) => WaitForDrainAsync(RemainingJobMembers, within);

    internal static async Task<uint> WaitForDrainAsync(Func<uint> members, TimeSpan within, Func<Task>? pause = null)
    {
        pause ??= () => Task.Delay(20);
        var deadline = DateTime.UtcNow + within;
        while (true)
        {
            var remaining = members();
            if (remaining == 0 || DateTime.UtcNow >= deadline)
            {
                return remaining;
            }

            await pause().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Terminal escalation. The handle stays open afterwards, because the count above is
    /// how termination gets confirmed, and kill-on-close stays as crash protection.
    /// </summary>
    public void TerminateJob() => job.Terminate(1);

    /// <summary>
    /// Terminates the job without throwing, for the paths whose job is to end the session
    /// no matter what — the confirmation afterwards is what decides the outcome.
    /// </summary>
    public void TerminateJobSafely()
    {
        try
        {
            job.Terminate(1);
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Releases the job and the ownership claim. Only the finalization run calls this,
    /// and only once writers are provably gone.
    /// </summary>
    public void ReleaseContainment()
    {
        job.Dispose();
        sessionLock.Dispose();
    }

    /// <summary>True while this session still holds the device.</summary>
    public bool OwnershipHeld => sessionLock.IsHeld;

    public void DisposeProcessResources()
    {
        Process?.Dispose();
        Process = null;
    }
}
