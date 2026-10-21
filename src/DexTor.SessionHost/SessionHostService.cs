using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Contracts;
using DexTor.Qemu;
using Microsoft.Extensions.Logging;

namespace DexTor.SessionHost;

/// <summary>Where a start has got. The Guest* stages come from what the guest's display shows while its window is still hidden.</summary>
public enum SessionStartStage
{
    CheckingUsb,
    CheckingRuntime,
    LockingStorage,
    StartingQemu,
    ConnectingControlChannel,

    /// <summary>The firmware and the boot loader are on the screen (text mode).</summary>
    GuestFirmware,

    /// <summary>The kernel has the display and the system is loading; nothing to show yet.</summary>
    GuestLoadingSystem,

    /// <summary>DexTor OS has drawn its splash or desktop; its window opens.</summary>
    GuestShowing,

    /// <summary>The guest has taken several minutes without a picture; reported again every few minutes. Not a step.</summary>
    GuestSlow,
    Running
}

public sealed record SessionStartProgress(
    SessionStartStage Stage,
    long BytesDownloaded = 0,
    long? TotalBytes = null,
    double? BytesPerSecond = null,
    TimeSpan? EstimatedRemaining = null);

public interface ISessionHostService
{
    Task StartAsync(string usbPath, IProgress<SessionStartProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>Stops and finalizes the session. Never ejects.</summary>
    Task<SessionFinalizationReport> StopAsync(
        IProgress<SessionFinalizationStage>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Stops, finalizes, and then asks Windows to remove the device.</summary>
    Task<SessionFinalizationReport> StopAndEjectAsync(
        IProgress<SessionFinalizationStage>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A standalone eject after a session has already been stopped. Re-establishes
    /// ownership and revalidates the device rather than trusting an earlier result.
    /// </summary>
    Task<SessionFinalizationReport> EjectAsync(
        string usbPath,
        IProgress<SessionFinalizationStage>? progress = null,
        CancellationToken cancellationToken = default);

    Task EmergencyStopAsync(CancellationToken cancellationToken = default);

    SessionState State { get; }
}

public sealed class SessionHostService : ISessionHostService, IAsyncDisposable
{
    private static readonly Action<ILogger, string, Exception?> LogSessionOwnershipAcquired =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1102, nameof(LogSessionOwnershipAcquired)),
            "Acquired machine-wide DexTor VM session ownership {Fingerprint}.");
    private static readonly Action<ILogger, string, Exception?> LogSessionOwnershipRecovered =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(1103, nameof(LogSessionOwnershipRecovered)),
            "Recovered DexTor VM session ownership {Fingerprint} from a launcher that exited without releasing it.");
    private static readonly Action<ILogger, string, Exception?> LogSessionOwnershipReleased =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1104, nameof(LogSessionOwnershipReleased)),
            "Released DexTor VM session ownership {Fingerprint}.");
    private static readonly Action<ILogger, string, Exception?> LogPreviousSession =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(1105, nameof(LogPreviousSession)),
            "Previous DexTor session for this device: {Disposition}.");

    private readonly ILogger<SessionHostService>? _logger;
    private readonly string _runtimeRoot;
    private readonly string _runtimeManifestPath;
    private readonly SessionJournal _journal;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Func<QemuLaunchPlan, QemuJobObject, CancellationToken, IQemuProcess> _startProcess;
    private readonly Func<IQemuProcess, Func<CancellationToken, Task<GuestScreen>>, CancellationToken, VmWindowOptions?, Task<bool>> _revealWindow;
    private readonly bool _realQemu;
    private readonly ShutdownPolicy _policy;
    private readonly IHostVolumeFlusher _flusher;
    private readonly IRemovableDeviceResolver _resolver;
    private readonly IDeviceRemover _remover;

    private SessionGeneration? _generation;

    /// <summary>
    /// The USB this service last started from, so a standalone eject after the session has
    /// finished still knows which device the operator means.
    /// </summary>
    private string? _lastUsbPath;
    private Task<string>? _standardError;
    private string? _lastQmpError;
    private SessionState _state = SessionState.Idle;

    public SessionHostService(
        ILogger<SessionHostService>? logger = null,
        string? runtimeRoot = null,
        string? runtimeManifestPath = null,
        string? journalPath = null)
        : this(logger, runtimeRoot, runtimeManifestPath, journalPath, startProcess: null)
    {
    }

    /// <summary>
    /// Test seam for fault injection around an already-created process, and for the host
    /// adapters.
    /// </summary>
    /// <remarks>
    /// The process seam receives the job the child must belong to, so a substitute cannot
    /// sidestep it by accident; the default is <see cref="QemuProcessLauncher.Launch"/>
    /// and nothing in the product selects anything else. The adapter seams exist so tests
    /// never touch a real volume or a real device — their defaults are the real Windows
    /// adapters, and no substitute is reachable from the product.
    /// </remarks>
    internal SessionHostService(
        ILogger<SessionHostService>? logger,
        string? runtimeRoot,
        string? runtimeManifestPath,
        string? journalPath,
        Func<QemuLaunchPlan, QemuJobObject, CancellationToken, IQemuProcess>? startProcess,
        ShutdownPolicy? policy = null,
        IHostVolumeFlusher? flusher = null,
        IRemovableDeviceResolver? resolver = null,
        IDeviceRemover? remover = null)
    {
        _startProcess = startProcess ?? QemuProcessLauncher.Launch;
        // Only a real QEMU has a window to wait for; a substitute process has none.
        _revealWindow = startProcess is null ? QemuWindowManager.RevealWhenDrawingAsync : static (_, _, _, _) => Task.FromResult(true);
        _realQemu = startProcess is null;
        // Likewise only a real QEMU is probed for a faster profile; a substitute is always
        // started the way every computer takes.
        _profiles = startProcess is null ? VmProfileSelector.CreateDefault() : null;
        _policy = policy ?? ShutdownPolicy.Default;
        _flusher = flusher ?? new WindowsVolumeFlusher();
        _resolver = resolver ?? new WindowsRemovableDeviceResolver();
        _remover = remover ?? new WindowsDeviceRemover();
        _logger = logger;
        _runtimeRoot = runtimeRoot ?? ResolveDefaultRuntimeRoot(AppContext.BaseDirectory, DexTorStorage.Runtime);
        _runtimeManifestPath = runtimeManifestPath ?? ResolveDefaultRuntimeManifestPath(_runtimeRoot, AppContext.BaseDirectory);
        _journal = new SessionJournal(
            journalPath
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DexTor", "session.json"));
    }

    /// <summary>The wizard's language (en, ru, uk), passed to the guest at the next start.</summary>
    public string? GuestLanguage { get; set; }

    /// <summary>The launcher's monitor, for the guest's first mode and the VM window; null leaves QEMU's defaults.</summary>
    public VmDisplay? Display { get; set; }

    /// <summary>The splash the VM window is revealed behind; null shows it directly.</summary>
    public IBootCurtain? BootCurtain { get; set; }

    private volatile bool _showWindowNow;

    /// <summary>Shows the VM window whatever is on it - the operator's choice after a slow start.</summary>
    public void ShowWindowNow() => _showWindowNow = true;

    private GuestSignals? _signals;

    /// <summary>
    /// The POSIX locale for a wizard language - every one of the wizard's ten - handed to the
    /// guest as fw_cfg opt/org.dextoros/locale; null for anything else, so the guest keeps its
    /// own default. DexTor OS maps each of these to its own translation.
    /// </summary>
    /// <summary>
    /// The boot stage a screendump shows, given the stage already reached: text mode is the
    /// firmware and GRUB; QEMU's placeholder or a blank graphics mode after that is the kernel
    /// taking the display; a picture is the splash. Null when it says nothing new.
    /// </summary>
    private static async Task CloseSignalsAsync(Task<GuestSignals?> signals)
    {
        try
        {
            if (await signals.ConfigureAwait(false) is { } open) await open.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    /// <summary>The guest's signal channel, or null when it cannot be had - the screendumps then decide alone.</summary>
    private static async Task<GuestSignals?> ConnectSignalsAsync(string pipeName, CancellationToken cancellationToken)
    {
        try
        {
            return await GuestSignals.ConnectAsync(pipeName, TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static SessionStartStage? BootStageOf(GuestScreen screen, SessionStartStage reached) => screen switch
    {
        GuestScreen.TextMode => SessionStartStage.GuestFirmware,
        GuestScreen.Blank => SessionStartStage.GuestLoadingSystem,
        GuestScreen.Placeholder when reached >= SessionStartStage.GuestFirmware => SessionStartStage.GuestLoadingSystem,
        GuestScreen.Picture => SessionStartStage.GuestShowing,
        _ => null
    };

    internal static string? ToGuestLocale(string? language) => language switch
    {
        "en" => "en_US.UTF-8",
        "ru" => "ru_RU.UTF-8",
        "uk" => "uk_UA.UTF-8",
        "de" => "de_DE.UTF-8",
        "es" => "es_ES.UTF-8",
        "fr" => "fr_FR.UTF-8",
        "pt-BR" => "pt_BR.UTF-8",
        "pl" => "pl_PL.UTF-8",
        "tr" => "tr_TR.UTF-8",
        "zh-CN" => "zh_CN.UTF-8",
        _ => null
    };

    public SessionState State => _state;

    public string? LastError => _lastQmpError;

    private volatile bool _pausedForSpace;

    /// <summary>
    /// QEMU has paused the VM because the stick is full: a write to the sparse data.img found
    /// no room on the NTFS volume. Nothing is lost - the guest never saw the error - and
    /// <see cref="ResumeAfterSpaceFreedAsync"/> carries on once the operator has made room.
    /// </summary>
    /// <remarks>
    /// QEMU's default for -drive is werror=enospc: on ENOSPC it stops the VM, sends
    /// BLOCK_IO_ERROR with nospace=true and then STOP. That is the protection DexTor OS
    /// relies on (its storage has no read-only fallback), so it must not be mistaken for a
    /// hang or a crash, nor be changed to werror=report.
    /// </remarks>
    public bool IsPausedForSpace => _pausedForSpace;

    /// <summary>Lets a VM paused for want of space carry on; false when it could not be asked.</summary>
    public async Task<bool> ResumeAfterSpaceFreedAsync(CancellationToken cancellationToken = default)
    {
        if (!_pausedForSpace || _generation?.Qmp is not { } qmp) return false;
        var response = await qmp.ExecuteAsync("cont", TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return false;
        // Still no room and QEMU pauses again at once, with a new BLOCK_IO_ERROR.
        _pausedForSpace = false;
        if (_loggedSessionId is { } session) VmSessionLog.Note(session, "resumed after the stick ran out of space");
        return true;
    }

    /// <summary>A BLOCK_IO_ERROR that is QEMU pausing the VM for want of space, not a failure.</summary>
    internal static bool IsNoSpacePause(QmpEvent qmpEvent) =>
        string.Equals(qmpEvent.Event, QmpShutdownEvidence.BlockIoErrorEvent, StringComparison.Ordinal)
        && qmpEvent.Data is { ValueKind: System.Text.Json.JsonValueKind.Object } data
        && data.TryGetProperty("nospace", out var nospace) && nospace.ValueKind == System.Text.Json.JsonValueKind.True
        && (!data.TryGetProperty("action", out var action) || string.Equals(action.GetString(), "stop", StringComparison.Ordinal));

    /// <summary>What the journal said about the session before the current one.</summary>
    public PreviousSessionRecord? PreviousSession { get; private set; }

    internal static string ResolveDefaultRuntimeRoot(string applicationBaseDirectory, string runtimeCacheRoot)
    {
        var bundledRuntime = Path.Combine(Path.GetFullPath(applicationBaseDirectory), "runtime", "qemu");
        return File.Exists(Path.Combine(bundledRuntime, "qemu-system-x86_64.exe"))
            && Directory.Exists(Path.Combine(bundledRuntime, "share"))
                ? bundledRuntime
                : Path.Combine(Path.GetFullPath(runtimeCacheRoot), "qemu-11.1.0");
    }

    internal static string ResolveDefaultRuntimeManifestPath(string runtimeRoot, string applicationBaseDirectory)
    {
        var payloadManifest = Path.Combine(Path.GetFullPath(runtimeRoot), "qemu-runtime-files.json");
        return File.Exists(payloadManifest)
            ? payloadManifest
            : Path.Combine(Path.GetFullPath(applicationBaseDirectory), "runtime", "qemu-manifest.json");
    }

    public async Task StartAsync(
        string usbPath,
        IProgress<SessionStartProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await StartCoreAsync(usbPath, progress, forcedProfile: null, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) when ((_qemuExitedDuringStart || _guestNeverDrew)
            && _startedProfile is { } refused
            && refused.Name != QemuCompatibilityProfile.Compatible.Name
            && !cancellationToken.IsCancellationRequested)
        {
            // The faster profile passed its probe but QEMU still gave up with the real
            // machine. It is not offered again, and this start goes on with the profile
            // every computer takes instead of ending in an error.
            if (_startedExecutable is { } executable) _profiles?.Refuse(executable, refused);
            await StartCoreAsync(usbPath, progress, QemuCompatibilityProfile.Compatible, cancellationToken).ConfigureAwait(false);
        }
    }

    private static readonly TimeSpan EarlyGuestDeath = TimeSpan.FromMinutes(1);
    private DateTime _runningSinceUtc = DateTime.MinValue;
    private Guid? _loggedSessionId;
    private readonly VmProfileSelector? _profiles;
    private QemuCompatibilityProfile? _startedProfile;
    private string? _startedExecutable;
    private bool _qemuExitedDuringStart;
    private bool _guestNeverDrew;

    /// <summary>The profile the running (or last) session was started with.</summary>
    public string? VmProfileName => _startedProfile?.Name;

    private async Task StartCoreAsync(
        string usbPath,
        IProgress<SessionStartProgress>? progress,
        QemuCompatibilityProfile? forcedProfile,
        CancellationToken cancellationToken)
    {
        _qemuExitedDuringStart = false;
        _guestNeverDrew = false;
        _loggedSessionId = null;
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        SessionGeneration? generation = null;
        try
        {
            if (_generation?.Process is { HasExited: false })
            {
                throw new InvalidOperationException("A DexTor OS session is already running.");
            }

            progress?.Report(new(SessionStartStage.CheckingUsb));
            var request = BuildStartRequest(usbPath) with
            {
                GuestLocale = ToGuestLocale(GuestLanguage),
                BuildChannel = DexTorBuildInfo.Channel,
                DisplayWidth = Display?.Width,
                DisplayHeight = Display?.Height,
                DisplayScalePercent = Display?.ScalePercent
            };
            _lastUsbPath = usbPath;
            var systemImage = VmPrelaunchGate.ResolveSystemImage(request);
            progress?.Report(new(SessionStartStage.CheckingRuntime));
            var executable = VmPrelaunchGate.ValidateRuntime(_runtimeRoot, _runtimeManifestPath);
            _state = SessionState.StorageLocking;
            progress?.Report(new(SessionStartStage.LockingStorage));

            // Claim ownership of this device's session before touching the image, and keep
            // it for the whole VM lifetime. The exclusive open below stays a probe: QEMU
            // has to open data.img itself, so a held file handle would block the launch.
            var identity = DexTorSessionIdentity.ForDataImage(request.DataImagePath);
            var claim = DexTorSessionLock.TryAcquire(identity);
            var sessionLock = claim.Lock
                ?? throw new VmPrelaunchException(DexTorSessionLock.DescribeRefusal(claim));
            if (_logger is not null)
            {
                LogSessionOwnershipAcquired(_logger, identity.Fingerprint, null);
                if (sessionLock.RecoveredFromAbandonedSession)
                {
                    LogSessionOwnershipRecovered(_logger, identity.Fingerprint, null);
                }
            }

            // Read what the last session left behind, under ownership and before anything
            // replaces it. This is decided by the journal alone: an abandoned mutex says a
            // process died, never what it had written.
            var previous = _journal.ReadPrevious();
            PreviousSession = previous;
            if (_logger is not null)
            {
                LogPreviousSession(_logger, previous.Disposition.ToString(), null);
            }

            // The volume behind the image, captured now. Finalization resolves the device
            // from this rather than from a fresh look at the path, which a drive-letter
            // change could point somewhere else by then.
            var volumeGuidPath = WindowsVolumeFlusher.TryResolveVolumeRoot(request.DataImagePath);

            var job = QemuJobObject.CreateKillOnClose();
            generation = new SessionGeneration(
                request.SessionId,
                request.DataImagePath,
                volumeGuidPath,
                _journal,
                sessionLock,
                job);

            // Runs after ownership, and independently of it: an abandoned mutex never
            // stands in for evidence that no writer is still holding the image.
            using (VmPrelaunchGate.AcquireDataImageLease(request.DataImagePath)) { }

            var profile = forcedProfile
                ?? (_profiles is null
                    ? QemuCompatibilityProfile.Compatible
                    : await _profiles.SelectAsync(executable, cancellationToken).ConfigureAwait(false));
            _startedProfile = profile;
            _startedExecutable = executable;
            var plan = QemuLaunchPlanBuilder.Build(executable, systemImage, request, profile, hostAudio: !_realQemu || HostAudio.HasOutput());
            VmSessionLog.Started(request.SessionId, plan.ExecutablePath, plan.Arguments, profile.Name, request.MemoryMiB, request.ProcessorCount);
            _loggedSessionId = request.SessionId;
            _state = SessionState.StartingQemu;
            progress?.Report(new(SessionStartStage.StartingQemu));

            // The dirty record reaches the disk before any writer exists. If it cannot be
            // made durable the session does not start: a writer with no record of itself
            // is precisely what recovery cannot classify afterwards.
            if (!_journal.Write(new SessionJournalEntry(
                    request.SessionId,
                    "running",
                    Error: previous.RequiresCaution
                        ? $"Previous session disposition: {previous.Disposition}."
                        : null)))
            {
                throw new InvalidOperationException(
                    "DexTor could not record the session state durably, so it did not start the virtual machine. "
                    + (_journal.LastError ?? string.Empty));
            }

            // SDL's window counts physical pixels, as VmDisplayPlanner does; otherwise Windows
            // would stretch it on a scaled monitor.
            if (_realQemu) Environment.SetEnvironmentVariable("SDL_WINDOWS_DPI_AWARENESS", "permonitorv2");
            // The job is named in the child's creation attributes, so QEMU is a member
            // from its first instruction.
            var process = _startProcess(plan, job, cancellationToken)
                ?? throw new InvalidOperationException("QEMU could not be started.");
            generation.Process = process;
            _generation = generation;
            // QEMU waits for this pipe before it starts the guest: connected at once.
            _showWindowNow = false;
            _signals = null;
            var signalsTask = _realQemu
                ? ConnectSignalsAsync(QemuLaunchPlanBuilder.ControlPipeName(request.SessionId), cancellationToken)
                : Task.FromResult<GuestSignals?>(null);

            _ = QemuWindowManager.KeepBrandedAsync(process, QemuLaunchPlanBuilder.VmWindowTitle, TimeSpan.FromMilliseconds(250));
            // The window is hidden from its first moment and shown once the guest draws;
            // screendumps can only be asked for once QMP has answered.
            QmpSession? displayChannel = null;
            // What the hidden screen shows is how far the guest has got; the wizard shows it as
            // steps, so the wait before the window opens never looks like a hang.
            var bootStage = SessionStartStage.ConnectingControlChannel;
            async Task<GuestScreen> ReadGuestScreenAsync(CancellationToken token)
            {
                var screen = displayChannel is { } channel ? await GuestDisplay.ReadAsync(channel, token).ConfigureAwait(false) : GuestScreen.Unknown;
                if (BootStageOf(screen, bootStage) is { } next && next > bootStage)
                {
                    bootStage = next;
                    progress?.Report(new(next));
                }

                return screen;
            }

            var windowOptions = new VmWindowOptions(
                Display,
                BootCurtain,
                () => signalsTask.IsCompletedSuccessfully ? (_signals = signalsTask.Result) : null,
                () => _showWindowNow,
                () => progress?.Report(new(SessionStartStage.GuestSlow)));
            var reveal = _revealWindow(process, ReadGuestScreenAsync, cancellationToken, windowOptions);
            process.Exited += (_, _) => OnProcessExited(generation);
            process.Exited += (_, _) => _ = CloseSignalsAsync(signalsTask);
            _standardError = process.StandardError.ReadToEndAsync(CancellationToken.None);

            progress?.Report(new(SessionStartStage.ConnectingControlChannel));

            // One reader for the whole session, subscribed before anything can be
            // observed, so a SHUTDOWN event arriving just before exit is not lost to a
            // reader started later.
            var qmp = new QmpSession(process.StandardOutput, process.StandardInput);
            qmp.EventReceived += qmpEvent => OnQmpEvent(generation, qmpEvent);
            qmp.Start();
            generation.Qmp = qmp;

            await qmp.HandshakeAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            displayChannel = qmp;
            if (process.HasExited)
            {
                throw new InvalidOperationException($"QEMU exited during startup with code {process.ExitCode}.");
            }

            generation.Finalizer = new SessionFinalizer(generation, _policy, _flusher, _resolver, _remover);
            _state = SessionState.Running;
            _pausedForSpace = false;
            _runningSinceUtc = DateTime.UtcNow;
            // The wizard steps aside when it hears "running"; heard before QEMU had a window,
            // it left the operator with nothing on screen for a few seconds.
            if (!await reveal.ConfigureAwait(false) && profile.Name != QemuCompatibilityProfile.Compatible.Name)
            {
                // Hung in the firmware with this CPU profile, as -cpu host did on an Alder
                // Lake: QEMU runs on and would show a black window for ever. The start is
                // redone with the profile every computer takes.
                _guestNeverDrew = true;
                throw new InvalidOperationException($"The guest drew nothing in {QemuWindowManager.DisplayWaitTimeout.TotalSeconds:0} s with the {profile.Name} CPU profile.");
            }

            VmSessionLog.Note(request.SessionId, "running: QMP answered and the guest is drawing");
            progress?.Report(new(SessionStartStage.Running));
            _lastQmpError = null;
        }
        catch (Exception exception)
        {
            // Read before cleanup, which ends the process itself.
            _qemuExitedDuringStart = generation?.Process is { HasExited: true };
            var unconfirmed = await CleanupFailedStartAsync(generation, exception.Message).ConfigureAwait(false);
            var diagnostics = ReadStartupDiagnostics();
            if (_loggedSessionId is { } failedSession)
            {
                VmSessionLog.Note(failedSession, $"start failed: {exception.GetType().Name}: {exception.Message}"
                    + (_qemuExitedDuringStart ? " (QEMU had exited)" : string.Empty)
                    + (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : $" QEMU: {diagnostics}"));
            }

            _standardError = null;

            // An uncontained writer outranks cancellation: the caller must hear that the
            // device is still claimed, not just that it asked to stop.
            if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested && unconfirmed is null) throw;
            var message = string.IsNullOrWhiteSpace(diagnostics) ? exception.Message : $"{exception.Message} QEMU: {diagnostics}";
            throw new InvalidOperationException(
                unconfirmed is null
                    ? message
                    : $"{message} {unconfirmed} DexTor is still holding this device because a virtual machine may still be writing to it.",
                exception);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task<SessionFinalizationReport> StopAsync(
        IProgress<SessionFinalizationStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        FinalizeAsync(requestEject: false, progress, cancellationToken);

    public Task<SessionFinalizationReport> StopAndEjectAsync(
        IProgress<SessionFinalizationStage>? progress = null,
        CancellationToken cancellationToken = default) =>
        FinalizeAsync(requestEject: true, progress, cancellationToken);

    /// <summary>
    /// The one join point. Every caller lands on the same run for the same generation.
    /// </summary>
    /// <remarks>
    /// Cancellation is split deliberately. A token already cancelled on arrival means the
    /// request was never accepted, so nothing starts. Once accepted, the token only stops
    /// <em>this caller</em> waiting — the run continues, because abandoning a half-finished
    /// shutdown would leave a writer nobody is tracking.
    /// </remarks>
    private async Task<SessionFinalizationReport> FinalizeAsync(
        bool requestEject,
        IProgress<SessionFinalizationStage>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var generation = _generation;
        if (generation?.Finalizer is null)
        {
            _state = SessionState.SafeToEject;
            var finished = new SessionFinalizationReport(WriterTermination: WriterTerminationResult.Confirmed);

            // Nothing is running, but the caller asked for an eject. Dropping that intent
            // because the session had already finished would silently turn a stop-and-eject
            // into a stop, so it is carried out as a subsequent owned operation.
            return requestEject && _lastUsbPath is { } path
                ? await EjectAsync(path, progress, cancellationToken).ConfigureAwait(false)
                : finished;
        }

        if (progress is not null)
        {
            generation.StageObserver = progress.Report;
        }

        _state = SessionState.Stopping;

        // The lifecycle lock is deliberately not held here: the run awaits process exit,
        // which is delivered by a callback that would need the same lock.
        var run = generation.Finalizer.RunAsync(requestEject);
        var report = await run.WaitAsync(cancellationToken).ConfigureAwait(false);
        await SettleAsync(generation, report).ConfigureAwait(false);

        // The eject may have been asked for after the run had already passed that step.
        // The intent is honoured as a following owned operation rather than dropped.
        if (requestEject
            && report.WriterTermination == WriterTerminationResult.Confirmed
            && report.DeviceRemoval is DeviceRemovalResult.NotRequested
            && _lastUsbPath is { } usbPath)
        {
            var removal = await EjectAsync(usbPath, progress, cancellationToken).ConfigureAwait(false);
            return report with
            {
                DeviceRemoval = removal.DeviceRemoval,
                DeviceRemovalDetail = removal.DeviceRemovalDetail
            };
        }

        return report;
    }

    public async Task<SessionFinalizationReport> EjectAsync(
        string usbPath,
        IProgress<SessionFinalizationStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A live session finalizes and ejects as one sequence, holding ownership across
        // both. This is the standalone case: nothing is running, so ownership has to be
        // taken again and the device revalidated. An earlier stop result is history, not
        // evidence about the device now.
        if (_generation?.Finalizer is not null)
        {
            return await FinalizeAsync(requestEject: true, progress, cancellationToken).ConfigureAwait(false);
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = BuildStartRequest(usbPath);
            var identity = DexTorSessionIdentity.ForDataImage(request.DataImagePath);
            var claim = DexTorSessionLock.TryAcquire(identity);
            if (claim.Lock is null)
            {
                return new SessionFinalizationReport(
                    WriterTermination: WriterTerminationResult.Unconfirmed,
                    DeviceRemoval: DeviceRemovalResult.Skipped,
                    DeviceRemovalDetail: DexTorSessionLock.DescribeRefusal(claim));
            }

            using var sessionLock = claim.Lock;
            progress?.Report(SessionFinalizationStage.RequestingRemoval);

            var resolution = _resolver.Resolve(request.DataImagePath);
            if (!resolution.Resolved || resolution.Target is null)
            {
                return new SessionFinalizationReport(
                    WriterTermination: WriterTerminationResult.Confirmed,
                    DeviceRemoval: DeviceRemovalResult.TargetUnresolved,
                    DeviceRemovalDetail: resolution.Reason);
            }

            var (result, detail) = await _remover
                .RequestRemovalAsync(resolution.Target, cancellationToken)
                .ConfigureAwait(false);
            progress?.Report(SessionFinalizationStage.Completed);
            return new SessionFinalizationReport(
                WriterTermination: WriterTerminationResult.Confirmed,
                DeviceRemoval: result,
                DeviceRemovalDetail: detail);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Publishes the outcome and releases containment, but only when nothing is left
    /// running or in flight.
    /// </summary>
    /// <remarks>
    /// This is the only place ownership is released for a session that started, and it is
    /// reached only from the finalization run. A process-exit callback never gets here on
    /// its own, so it cannot hand the device back while a caller is still stopping it.
    /// A settled failure still releases — a flush that was denied or a removal that was
    /// vetoed leaves no writer behind, so holding the device for ever would be its own
    /// bug. Only an unresolved writer keeps containment.
    /// </remarks>
    private async Task SettleAsync(SessionGeneration generation, SessionFinalizationReport report)
    {
        VmSessionLog.Note(generation.SessionId, $"session ended: guest {report.GuestShutdown}, escalation {report.Escalation}, writer {report.WriterTermination}, flush {report.Flush}");
        if (report.WriterTermination != WriterTerminationResult.Confirmed)
        {
            // Protected: the job and the claim stay, and the state never becomes a
            // success the operator could act on.
            _state = SessionState.Stopping;
            _lastQmpError = report.WriterTerminationDetail ?? _lastQmpError;
            return;
        }

        if (!generation.TryBeginSettle())
        {
            // Another caller of the same run already tore this generation down. They all
            // receive the same report; only one may dispose what backs it.
            _state = SessionState.SafeToEject;
            return;
        }

        if (generation.Qmp is not null)
        {
            await generation.Qmp.DisposeAsync().ConfigureAwait(false);
            generation.Qmp = null;
        }

        generation.CloseUsbHandles();
        generation.DisposeProcessResources();
        _standardError = null;

        if (ReferenceEquals(_generation, generation))
        {
            _generation = null;
        }

        var held = generation.OwnershipHeld;
        generation.ReleaseContainment();
        if (held && _logger is not null)
        {
            LogSessionOwnershipReleased(_logger, generation.SessionId.ToString(), null);
        }

        _state = SessionState.SafeToEject;
    }

    public async Task EmergencyStopAsync(CancellationToken cancellationToken = default)
    {
        const string reason = "The DexTor USB drive was removed while the virtual machine was running.";
        _lastQmpError = reason;

        var generation = _generation;
        if (generation?.Finalizer is null)
        {
            // Nothing was running, but the removal itself is worth recording: the next
            // start has to know the device left while DexTor was watching it.
            _journal.Write(new SessionJournalEntry(null, "emergency-usb-removed", Error: reason), durable: false);
            _state = SessionState.SafeToEject;
            return;
        }

        // The device is already gone, so there is nothing to flush or eject — but the
        // writer still has to be contained, and that is the same one operation.
        generation.TerminateJobSafely();
        var report = await generation.Finalizer.RunAsync(requestEject: false).WaitAsync(cancellationToken).ConfigureAwait(false);
        _journal.Write(
            new SessionJournalEntry(generation.SessionId, "emergency-usb-removed", report.ExitCode, reason, report),
            durable: false);
        await SettleAsync(generation, report).ConfigureAwait(false);
        _lastQmpError = reason;
    }

    public async ValueTask DisposeAsync()
    {
        var generation = _generation;
        if (generation is not null)
        {
            generation.TerminateJobSafely();
            if (generation.Finalizer is not null)
            {
                var report = await generation.Finalizer.RunAsync(requestEject: false).ConfigureAwait(false);
                await SettleAsync(generation, report).ConfigureAwait(false);
            }
            else
            {
                generation.CloseUsbHandles();
                generation.DisposeProcessResources();
                generation.ReleaseContainment();
                _generation = null;
            }
        }

        _lifecycle.Dispose();
    }

    /// <summary>
    /// QEMU exited on its own. This never releases ownership, disposes the job, marks the
    /// journal or publishes a state — it joins the one finalization run, which does.
    /// </summary>
    private void OnProcessExited(SessionGeneration generation)
    {
        if (!ReferenceEquals(generation, _generation))
        {
            // An old session's writer. It has no say over the session running now.
            return;
        }

        generation.ObserveProcessExit();
        _ = CaptureUnexpectedExitAsync(generation);
    }

    private async Task CaptureUnexpectedExitAsync(SessionGeneration generation)
    {
        string? diagnostics = null;
        try
        {
            diagnostics = FormatQemuDiagnostics(_standardError is null ? null : await _standardError.ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            diagnostics = exception.Message;
        }

        if (!ReferenceEquals(generation, _generation) || generation.Finalizer is null)
        {
            return;
        }

        if (generation.Finalizer.Started)
        {
            // A stop is already running; it will report the outcome.
            return;
        }

        var ranFor = DateTime.UtcNow - _runningSinceUtc;
        // Shut down from inside DexTor OS: QEMU leaving then is the normal end of a session,
        // not a failure, and it says nothing against the CPU profile.
        if (generation.ObservedShutdown == GuestShutdownEvidence.ObservedGuestInitiated)
        {
            _lastQmpError = null;
            VmSessionLog.Note(generation.SessionId, $"guest shut down after {ranFor.TotalSeconds:0} s"
                + (string.IsNullOrWhiteSpace(diagnostics) ? string.Empty : $"; QEMU: {diagnostics}"));
        }
        else
        {
            // A guest that dies within a minute of starting, without being asked to stop,
            // points at the CPU profile: the next start does without it.
            if (ranFor < EarlyGuestDeath
                && _startedProfile is { } profile && _startedExecutable is { } executable)
            {
                _profiles?.Refuse(executable, profile);
            }

            _lastQmpError = string.IsNullOrWhiteSpace(diagnostics)
                ? generation.LastError ?? "QEMU closed unexpectedly."
                : diagnostics;
            VmSessionLog.Note(generation.SessionId, $"QEMU closed unexpectedly after {ranFor.TotalSeconds:0} s: {_lastQmpError}");
        }

        var report = await generation.Finalizer.RunAsync(requestEject: false).ConfigureAwait(false);
        await SettleAsync(generation, report).ConfigureAwait(false);
    }

    private void OnQmpEvent(SessionGeneration generation, QmpEvent qmpEvent)
    {
        if (!ReferenceEquals(generation, _generation))
        {
            // A late event from a finished session. Recording it would let an old run
            // change what the current one reports.
            return;
        }

        generation.Observe(qmpEvent);

        if (IsNoSpacePause(qmpEvent))
        {
            if (!_pausedForSpace && _loggedSessionId is { } session) VmSessionLog.Note(session, "paused: the stick is full (BLOCK_IO_ERROR nospace)");
            _pausedForSpace = true;
            return;
        }

        if (string.Equals(qmpEvent.Event, "RESUME", StringComparison.Ordinal))
        {
            _pausedForSpace = false;
            return;
        }

        if (string.Equals(qmpEvent.Event, QmpShutdownEvidence.BlockIoErrorEvent, StringComparison.Ordinal))
        {
            var detail = qmpEvent.Data is { } data && data.TryGetProperty("reason", out var reason)
                ? $"QEMU storage error: {reason.GetString()}"
                : "QEMU reported a storage I/O error.";
            generation.LastError = detail;
            _lastQmpError = detail;
        }
    }

    /// <summary>
    /// Unwinds a start that failed, and reports whether the writer is provably gone.
    /// </summary>
    /// <remarks>
    /// Ownership is released only when termination is confirmed. A QEMU that may still be
    /// alive is a writer on <c>data.img</c>, and handing the device back while that is
    /// unknown is exactly how a second writer starts.
    /// </remarks>
    private async Task<string?> CleanupFailedStartAsync(SessionGeneration? generation, string error)
    {
        if (generation is null)
        {
            return null;
        }

        string? unconfirmed = null;
        var process = generation.Process;

        // Terminate the job unconditionally. A child may exist even when this service
        // never received its handle — a launcher seam that threw after creating one, for
        // instance — and the job is the only thing that knows about it either way.
        generation.TerminateJobSafely();

        if (process is not null)
        {
            try
            {
                if (!process.WaitForExit(_policy.TerminateTimeout))
                {
                    unconfirmed = "QEMU did not exit after it was terminated.";
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                unconfirmed = $"QEMU could not be confirmed terminated: {exception.Message}";
            }
        }

        if (unconfirmed is null)
        {
            // A terminate call is not evidence; an empty job is. Give the kernel a bounded
            // moment to reap what it was asked to kill before deciding.
            var deadline = DateTime.UtcNow + _policy.TerminateTimeout;
            while (true)
            {
                try
                {
                    var remaining = generation.RemainingJobMembers();
                    if (remaining == 0)
                    {
                        break;
                    }

                    if (DateTime.UtcNow >= deadline)
                    {
                        unconfirmed = $"{remaining} process(es) of this session are still running.";
                        break;
                    }
                }
                catch (Win32Exception exception)
                {
                    unconfirmed = $"The session job could not be queried: {exception.Message}";
                    break;
                }

                Thread.Sleep(25);
            }
        }

        _journal.Write(
            new SessionJournalEntry(
                generation.SessionId,
                "start-failed",
                unconfirmed is null && process is { HasExited: true } ? TryExitCode(process) : null,
                unconfirmed is null ? error : $"{error} {unconfirmed}"),
            durable: false);

        if (generation.Qmp is not null)
        {
            await generation.Qmp.DisposeAsync().ConfigureAwait(false);
            generation.Qmp = null;
        }

        if (unconfirmed is not null)
        {
            // Keep the process handle, the job and the claim: all three are what a later
            // stop attempt, and the next launcher, have to go on.
            _state = SessionState.Idle;
            return unconfirmed;
        }

        generation.CloseUsbHandles();
        generation.DisposeProcessResources();
        generation.ReleaseContainment();
        if (ReferenceEquals(_generation, generation))
        {
            _generation = null;
        }

        _state = SessionState.Idle;
        return null;
    }

    private static int? TryExitCode(IQemuProcess process)
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

    internal static SessionStartRequest BuildStartRequest(string usbPath)
    {
        var root = Path.GetFullPath(usbPath);
        var metadataPath = Path.Combine(root, "DEXTOR", "metadata", "device.json");
        if (!File.Exists(metadataPath)) throw new InvalidOperationException("DexTor USB metadata is missing.");
        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        var metadata = document.RootElement;
        if (!string.Equals(metadata.GetProperty("state").GetString(), "ready", StringComparison.Ordinal))
            throw new InvalidOperationException("DexTor USB has not passed final verification.");

        var expectedHash = metadata.GetProperty("systemImageSha256").GetString() ?? string.Empty;
        var usbSystem = ResolveUsbPath(root, metadata.GetProperty("systemImagePath").GetString());
        var dataImage = ResolveUsbPath(root, metadata.GetProperty("dataImagePath").GetString());
        var preferred = FindMatchingCachedImage(expectedHash) ?? usbSystem;
        var requestId = metadata.TryGetProperty("requestId", out var requestIdElement)
            && requestIdElement.ValueKind == JsonValueKind.String
                ? requestIdElement.GetString()
                : null;
        var (memoryMiB, processors) = VmResources.ForThisComputer();
        return new SessionStartRequest(Guid.NewGuid(), preferred, usbSystem, dataImage, expectedHash, memoryMiB, processors, requestId);
    }

    private static string ResolveUsbPath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) throw new InvalidOperationException("DexTor USB metadata contains an invalid payload path.");
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("DexTor USB metadata points outside the USB drive.");
        return resolved;
    }

    private static string? FindMatchingCachedImage(string expectedHash)
    {
        var root = DexTorStorage.LiveImage;
        if (!Directory.Exists(root)) return null;
        foreach (var path in Directory.EnumerateFiles(root, "*.iso"))
        {
            using var stream = File.OpenRead(path);
            if (string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedHash, StringComparison.OrdinalIgnoreCase)) return path;
        }
        return null;
    }

    internal static bool IsUnexpectedStopEvent(string eventName) =>
        string.Equals(eventName, QmpShutdownEvidence.StopEvent, StringComparison.Ordinal)
        || string.Equals(eventName, QmpShutdownEvidence.PanicEvent, StringComparison.Ordinal);

    private string? ReadStartupDiagnostics()
    {
        try
        {
            return FormatQemuDiagnostics(_standardError?.GetAwaiter().GetResult());
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    internal static string? FormatQemuDiagnostics(string? standardError)
    {
        var text = standardError?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Warnings are not the reason QEMU stopped. "host doesn't support requested
        // feature: CPUID...svm" is printed on every start under WHPX and used to be reported
        // as "the CPU profile is not supported" - after a guest had simply been shut down.
        var meaningfulLine = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => !line.Contains("warning:", StringComparison.OrdinalIgnoreCase));
        if (meaningfulLine is null) return null;
        return meaningfulLine.Length <= 400 ? meaningfulLine : meaningfulLine[..400];
    }
}
