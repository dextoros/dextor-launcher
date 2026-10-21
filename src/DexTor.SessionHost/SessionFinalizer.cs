using System.ComponentModel;
using DexTor.Contracts;
using DexTor.Qemu;

namespace DexTor.SessionHost;

/// <summary>Timings for the shutdown stages. Injectable so tests need no long sleeps.</summary>
public sealed record ShutdownPolicy(
    TimeSpan GuestShutdownTimeout,
    TimeSpan QuitTimeout,
    TimeSpan TerminateTimeout,
    TimeSpan CommandTimeout)
{
    /// <summary>The shipped policy: 60s for the guest, 10s after quit, 10s after terminate.</summary>
    public static ShutdownPolicy Default { get; } = new(
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5));
}

/// <summary>
/// Runs one session's shutdown, host finalization and — when asked — device removal.
/// </summary>
/// <remarks>
/// Exactly one of these exists per session generation, and every caller joins the same
/// run: <c>StopAsync</c>, a spontaneous process exit, a UI request and a disconnect all
/// converge here. That is what stops a process-exit callback from independently releasing
/// ownership, disposing the job or publishing a success the stopping caller never agreed
/// to.
/// <para>
/// Nothing in here holds the service's lifecycle lock. The run awaits process exit, which
/// is reported by a callback that would need that lock; taking it across the wait would
/// deadlock the session it is trying to end.
/// </para>
/// </remarks>
internal sealed class SessionFinalizer(
    SessionGeneration generation,
    ShutdownPolicy policy,
    IHostVolumeFlusher flusher,
    IRemovableDeviceResolver resolver,
    IDeviceRemover remover)
{
    private readonly object _gate = new();
    private Task<SessionFinalizationReport>? _run;
    private bool _ejectRequested;

    /// <summary>
    /// Starts finalization, or joins the run already in progress.
    /// </summary>
    /// <param name="requestEject">
    /// When true, the run also asks Windows to remove the device once writers are gone.
    /// A join that asks for eject upgrades a run that had not been asked — stop then
    /// eject is one sequence, not two competing ones.
    /// </param>
    /// <remarks>
    /// A run in progress is joined, never duplicated. A run that <em>finished</em> is
    /// returned again only when it actually finished the job: an attempt that could not
    /// confirm the writers were gone left the session still owned and still containing
    /// something, so a later request must be allowed to try again rather than be handed
    /// the old "unconfirmed" for ever. That is what keeps an unconfirmed termination from
    /// leaking ownership permanently once the process does eventually exit.
    /// </remarks>
    public Task<SessionFinalizationReport> RunAsync(bool requestEject)
    {
        lock (_gate)
        {
            if (requestEject)
            {
                _ejectRequested = true;
            }

            if (_run is null || !_run.IsCompleted)
            {
                return _run ??= Task.Run(ExecuteAsync);
            }

            var previous = _run.Status == TaskStatus.RanToCompletion ? _run.Result : null;
            var finished = previous is { WriterTermination: WriterTerminationResult.Confirmed };
            return finished ? _run : _run = Task.Run(ExecuteAsync);
        }
    }

    /// <summary>True once a run has been started, whatever its state.</summary>
    public bool Started
    {
        get
        {
            lock (_gate)
            {
                return _run is not null;
            }
        }
    }

    private bool EjectRequested
    {
        get
        {
            lock (_gate)
            {
                return _ejectRequested;
            }
        }
    }

    private async Task<SessionFinalizationReport> ExecuteAsync()
    {
        var report = new SessionFinalizationReport(
            GuestShutdown: generation.ObservedShutdown,
            GuestStorageClose: GuestStorageCloseEvidence.UnconfirmedNoAgent);

        generation.Report(SessionFinalizationStage.StoppingGuest);
        report = await StopWriterAsync(report).ConfigureAwait(false);

        // Everything below needs the writers to be provably gone. An unconfirmed writer
        // may still be holding data.img, so flushing, ejecting or handing the device back
        // would all be acting on an assumption rather than a fact.
        if (report.WriterTermination != WriterTerminationResult.Confirmed)
        {
            generation.Report(SessionFinalizationStage.TerminationUnconfirmed);
            report = report with
            {
                Flush = HostFlushResult.NotAttempted,
                DeviceRemoval = DeviceRemovalResult.Skipped,
                Journal = PersistOutcome(report, hostFinalized: false)
            };
            return report;
        }

        generation.Report(SessionFinalizationStage.FlushingStorage);
        report = await FlushAsync(report).ConfigureAwait(false);

        // The journal is written before the device can go away, and — when it lives on
        // the USB itself — before the flush that has to cover it.
        report = report with { Journal = PersistOutcome(report, hostFinalized: true) };

        if (EjectRequested)
        {
            generation.Report(SessionFinalizationStage.RequestingRemoval);
            report = await RemoveAsync(report).ConfigureAwait(false);
        }

        generation.Report(SessionFinalizationStage.Completed);
        return report;
    }

    /// <summary>
    /// Walks the escalation stages, skipping any the writer has already satisfied.
    /// </summary>
    private async Task<SessionFinalizationReport> StopWriterAsync(SessionFinalizationReport report)
    {
        var process = generation.Process;
        if (process is null)
        {
            await DrainJobQuietlyAsync().ConfigureAwait(false);
            return report with
            {
                WriterTermination = ConfirmGone(out var detail) ? WriterTerminationResult.Confirmed : WriterTerminationResult.Unconfirmed,
                WriterTerminationDetail = detail,
                GuestShutdown = generation.ObservedShutdown
            };
        }

        if (!process.HasExited)
        {
            // Stage 1. The acknowledgement is bounded separately from the guest's own
            // shutdown: a command that goes unanswered does not mean the guest ignored it.
            var qmp = generation.Qmp;
            if (qmp is not null)
            {
                await qmp.ExecuteAsync("system_powerdown", policy.CommandTimeout, CancellationToken.None).ConfigureAwait(false);
            }

            report = report.WithEscalation(ShutdownEscalation.PowerButton);
            await WaitForExitAsync(process, policy.GuestShutdownTimeout).ConfigureAwait(false);
        }

        if (!process.HasExited)
        {
            // Stage 2.
            generation.Report(SessionFinalizationStage.ForcingStop);
            var qmp = generation.Qmp;
            if (qmp is not null)
            {
                await qmp.ExecuteAsync("quit", policy.CommandTimeout, CancellationToken.None).ConfigureAwait(false);
            }

            report = report.WithEscalation(ShutdownEscalation.Quit);
            await WaitForExitAsync(process, policy.QuitTimeout).ConfigureAwait(false);
        }

        // Only what is still in the job after it has had a moment to empty outlived QEMU;
        // QEMU's own console host leaving a beat later is not a reason to force anything.
        if (!process.HasExited || await generation.WaitForJobToDrainAsync(JobDrainGrace).ConfigureAwait(false) > 0)
        {
            // Stage 3. The job defines the session's process set, so terminating it is
            // what reaches a descendant that outlived QEMU itself.
            generation.Report(SessionFinalizationStage.ForcingStop);
            try
            {
                generation.TerminateJob();
                report = report.WithEscalation(ShutdownEscalation.JobTerminated);
            }
            catch (Win32Exception exception)
            {
                return report with
                {
                    WriterTermination = WriterTerminationResult.Unconfirmed,
                    WriterTerminationDetail = $"The session could not be terminated: {exception.Message}"
                };
            }

            await WaitForExitAsync(process, policy.TerminateTimeout).ConfigureAwait(false);
        }

        if (process.HasExited)
        {
            await DrainJobQuietlyAsync().ConfigureAwait(false);
        }

        var confirmed = ConfirmGone(out var why);
        return report with
        {
            GuestShutdown = generation.ObservedShutdown is GuestShutdownEvidence.None && process.HasExited
                ? GuestShutdownEvidence.ProcessExitOnly
                : generation.ObservedShutdown,
            WriterTermination = confirmed ? WriterTerminationResult.Confirmed : WriterTerminationResult.Unconfirmed,
            WriterTerminationDetail = why,
            ExitCode = confirmed ? TryReadExitCode(process) : null
        };
    }

    /// <summary>How long the job may take to empty after QEMU itself has exited.</summary>
    internal static readonly TimeSpan JobDrainGrace = TimeSpan.FromSeconds(3);

    /// <summary>Lets the job empty before it is judged; a query failure is judged by ConfirmGone.</summary>
    private async Task DrainJobQuietlyAsync()
    {
        try
        {
            await generation.WaitForJobToDrainAsync(JobDrainGrace).ConfigureAwait(false);
        }
        catch (Win32Exception)
        {
            // ConfirmGone queries again and reports the failure as unconfirmed.
        }
    }

    /// <summary>
    /// Waits for the process, or for the timeout. Exceeding it never means the writer
    /// stopped — only that it had not stopped yet.
    /// </summary>
    private static async Task WaitForExitAsync(IQemuProcess process, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Two independent facts: the original process handle is signalled, and the session
    /// job holds nobody. A successful terminate call is not one of them.
    /// </summary>
    private bool ConfirmGone(out string? detail)
    {
        detail = null;
        var process = generation.Process;
        if (process is not null && !process.HasExited)
        {
            detail = "QEMU has not exited.";
            return false;
        }

        try
        {
            var remaining = generation.RemainingJobMembers();
            if (remaining > 0)
            {
                detail = $"{remaining} process(es) of this session are still running.";
                return false;
            }
        }
        catch (Win32Exception exception)
        {
            detail = $"The session job could not be queried: {exception.Message}";
            return false;
        }

        return true;
    }

    private static int? TryReadExitCode(IQemuProcess process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<SessionFinalizationReport> FlushAsync(SessionFinalizationReport report)
    {
        // Our own handles on the image must be gone first, or the flush would be racing
        // writes we ourselves still have open.
        generation.CloseUsbHandles();

        try
        {
            var (result, detail) = await flusher
                .FlushVolumeAsync(generation.DataImagePath, CancellationToken.None)
                .ConfigureAwait(false);
            return report with { Flush = result, FlushDetail = detail };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return report with { Flush = HostFlushResult.Failed, FlushDetail = exception.Message };
        }
    }

    private async Task<SessionFinalizationReport> RemoveAsync(SessionFinalizationReport report)
    {
        // A denied flush is not a reason to refuse to ask. The two outcomes stay separate
        // and both stay visible; neither is rewritten as the other.
        // Bound to the volume this session took, not to whatever the path resolves to now.
        var resolution = resolver.Resolve(generation.DataImagePath, generation.VolumeGuidPath);
        if (!resolution.Resolved || resolution.Target is null)
        {
            return report with
            {
                DeviceRemoval = DeviceRemovalResult.TargetUnresolved,
                DeviceRemovalDetail = resolution.Reason
            };
        }

        try
        {
            var (result, detail) = await remover
                .RequestRemovalAsync(resolution.Target, CancellationToken.None)
                .ConfigureAwait(false);
            return report with { DeviceRemoval = result, DeviceRemovalDetail = detail };
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return report with { DeviceRemoval = DeviceRemovalResult.Failed, DeviceRemovalDetail = exception.Message };
        }
    }

    /// <summary>
    /// Records the outcome, and nothing more than the outcome.
    /// </summary>
    /// <remarks>
    /// After a successful removal the device is gone, so no further USB write is even
    /// attempted; the record is written before removal is requested and the removal result
    /// is kept in memory for the caller. When the journal lives on the host — the default
    /// — that ordering costs nothing and is kept anyway, so both layouts behave the same.
    /// </remarks>
    private JournalPersistenceResult PersistOutcome(SessionFinalizationReport report, bool hostFinalized)
    {
        var state = report.WriterTermination == WriterTerminationResult.Confirmed
            ? report.WasForced ? "stopped-forced" : "stopped"
            : "termination-unconfirmed";

        var written = generation.Journal.Write(
            new SessionJournalEntry(
                generation.SessionId,
                state,
                report.ExitCode,
                report.WriterTerminationDetail ?? report.FlushDetail,
                report,
                hostFinalized));
        return written ? JournalPersistenceResult.Persisted : JournalPersistenceResult.Failed;
    }
}

/// <summary>Coarse progress, for the UI. Each stage is a separate outcome.</summary>
public enum SessionFinalizationStage
{
    StoppingGuest,
    ForcingStop,
    TerminationUnconfirmed,
    FlushingStorage,
    RequestingRemoval,
    Completed
}
