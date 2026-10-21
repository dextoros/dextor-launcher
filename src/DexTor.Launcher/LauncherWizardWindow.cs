using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.SessionHost;

using static DexTor.Launcher.WizardDesign;

namespace DexTor.Launcher;

internal enum LauncherWizardStep
{
    Intro,
    HostCheck,
    Image,
    Usb,
    Create,
    Complete
}

internal sealed record LauncherWizardEnvironment(PreflightReport Preflight, LauncherContext Context);

internal sealed class LauncherWizardDependencies
{
    internal required Func<Task<LauncherWizardEnvironment>> RefreshEnvironment { get; init; }
    internal required Func<Task<LauncherInlineActionResult>> PreparePc { get; init; }
    internal required Func<bool> RestartWindows { get; init; }
    internal required Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImageCacheInspectionResult>> InspectImage { get; init; }
    internal required Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>> PrepareImage { get; init; }
    internal required Func<CancellationToken, Task<IReadOnlyList<UsbDriveSnapshot>>> InspectUsbDrives { get; init; }

    /// <summary>
    /// Readies the host for rewriting the stick - closes Explorer on it, declines AutoPlay -
    /// and undoes that when disposed; null does nothing.
    /// </summary>
    internal Func<UsbDriveSnapshot, IDisposable?>? PrepareHostForCreation { get; init; }

    /// <summary>Measures a listed drive after the list is shown; null leaves speed out.</summary>
    internal Func<UsbDriveSnapshot, CancellationToken, Task<UsbAccessSpeedResult?>>? MeasureUsbSpeed { get; init; }
    internal required Func<string, long, bool> HasEnoughSpace { get; init; }
    internal required Func<UsbDriveSnapshot, bool> ConfirmErase { get; init; }
    internal required Func<UsbDriveSnapshot, IProgress<CreatorActionService.CreatorStepProgress>, Task<CreatorActionService.CreateUsbResult>> CreateUsb { get; init; }
    /// <summary>Asks a running USB creation to stop; false when there is none.</summary>
    internal Func<bool>? CancelCreation { get; init; }
    internal required Func<string, IProgress<DexTorUsbVerificationProgress>, CancellationToken, Task<DexTorUsbReadinessResult>> VerifyDexTorUsb { get; init; }
    internal required Func<string, IProgress<SessionStartProgress>, Task> StartDexTor { get; init; }
    internal required Func<IProgress<SessionFinalizationStage>?, Task<SessionFinalizationReport>> StopDexTor { get; init; }

    /// <summary>A separate action from stopping: it revalidates and asks Windows.</summary>
    internal required Func<string, IProgress<SessionFinalizationStage>?, Task<SessionFinalizationReport>> EjectDexTor { get; init; }
    internal required Func<Task> EmergencyStopDexTor { get; init; }
    internal required Func<string, bool> IsActiveUsbAvailable { get; init; }
    internal required Func<string, string, Task> ShowEmergencyNotification { get; init; }
    internal required Func<SessionState> GetSessionState { get; init; }
    internal required Func<string?> GetSessionError { get; init; }

    /// <summary>Bytes the stick lacks for the storage to reach its full size; null leaves the check out.</summary>
    internal Func<string, long>? StorageShortfall { get; init; }

    /// <summary>QEMU paused DexTor OS because the stick is full; null when it cannot tell.</summary>
    internal Func<bool>? IsVmPausedForSpace { get; init; }

    /// <summary>Lets DexTor OS carry on once there is room again.</summary>
    internal Func<Task<bool>>? ResumeDexTor { get; init; }

    /// <summary>Shows the VM window whatever is on it, after a slow start; null hides the choice.</summary>
    internal Action? ShowVmWindowNow { get; init; }
}

/// <summary>
/// The setup wizard, drawn to the designer's "DexTor OS Setup Wizard" screens (2026-10):
/// a 960 x 680 window, the wordmark and stepper at the top, one page per step, and the
/// footer with the brand line, the language list, the site link and two buttons.
/// </summary>
internal sealed partial class LauncherWizardWindow : Form
{
    private static readonly LauncherWizardStep[] Steps = Enum.GetValues<LauncherWizardStep>();
    private static readonly int QueryCancelAutoPlayMessage = RegisterWindowMessage("QueryCancelAutoPlay");

    /// <summary>The page column: 40 px in from both sides of the 960 px window.</summary>
    private const float ContentWidth = 880;

    /// <summary>A row inside a card: the card's width less its 16 px padding.</summary>
    private const float RowWidth = ContentWidth - 32;

    private readonly ILocalizer _localizer;
    private readonly LauncherWizardDependencies _dependencies;
    private readonly Control _root;
    private readonly Panel _contentHost;
    private readonly Panel _actionBar;
    private readonly System.Windows.Forms.Timer _usbRefreshTimer;
    private readonly System.Windows.Forms.Timer _vmStateTimer;
    private readonly GlassButton _backButton;
    private readonly GlassButton _primaryButton;
    private readonly GlassButton _closeButton;
    private readonly Dictionary<LauncherWizardStep, StepCircle> _stepGlyphs = [];
    private readonly Dictionary<LauncherWizardStep, WizardText> _stepLabels = [];
    private readonly StepperView _stepper;
    private readonly WizardText _copyright;
    private readonly LauncherBranding.ThemedLanguageSelector _languageSelector;
    private readonly WizardText _siteLink;
    private LauncherWizardEnvironment? _environment;
    private LiveImagePreparationResult? _preparedImage;
    private LiveImagePreparationResult? _imageFailure;
    private LiveImageCacheInspectionResult? _imageInspection;
    private string? _selectedUsb;
    private LauncherWizardStep _step;
    private LauncherWizardStep? _failedStep;
    private bool _busy;
    private bool _acceptImageProgress;
    private bool _restartPending;
    private bool _creationSucceeded;
    private bool _usbOnly;
    private CreatorActionService.CreateUsbResult? _lastCreateResult;
    private bool _canCancel;
    private Func<Task>? _secondaryAction;
    private CancellationTokenSource? _activeOperationCancellation;
    private Label? _pageStatus;
    private Label? _pageDetail;
    private WizardRow? _statusRow;
    private WizardInfoPanel? _statusCard;
    private WizardProgressBar? _pageProgress;
    private TransferStatsView? _transferStats;
    private readonly Queue<(double Seconds, long Bytes)> _transferSamples = new();
    private readonly System.Diagnostics.Stopwatch _transferClock = System.Diagnostics.Stopwatch.StartNew();
    private string? _transferStage;
    private double? _smoothedSpeed;
    private UsbDriveField? _usbList;
    private WizardText? _usbStatus;
    private WizardText? _usbDetails;
    private RowStatusMark? _usbStatusMark;
    private FactGrid? _usbFacts;
    private GlassNotice? _usbEraseNotice;
    private IReadOnlyList<UsbDriveSnapshot> _usbSnapshots = [];
    private readonly Dictionary<string, UsbAccessSpeedResult?> _usbSpeeds = new(StringComparer.OrdinalIgnoreCase);
    private WizardInfoPanel? _usbSearch;
    private WizardInfoPanel? _usbInfo;
    private Control? _usbListHeader;
    private bool _usbDiscoveryCompleted;
    private bool _activatedBeforeDriveLetter;
    private (DateTime StartedUtc, Task<IReadOnlyList<UsbDriveSnapshot>> Discovery)? _usbPrefetch;
    private bool _usbRefreshInProgress;
    private bool _vmRunning;
    private bool _vmHasRun;
    private bool _ejectAvailable;
    private string? _lastVmUsbRoot;
    private bool _vmStopRequested;
    private bool _existingUsbVerified;
    private bool _emergencyShutdownInProgress;
    private string? _activeVmUsbRoot;

    internal LauncherWizardWindow(ILocalizer localizer, LauncherWizardDependencies dependencies)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));

        Text = localizer.T("launcher.wizard.window_title");
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        ClientSize = S(960, 648);
        // Opaque: the Mica-like tint is in the window colour itself (WizardDesign.MicaTint).
        Opacity = WindowOpacity;
        BackColor = WizardDesign.Window;
        ForeColor = WizardDesign.Text;
        DoubleBuffered = true;
        // Chinese needs its own face before any text is made; see WizardFonts.Cjk.
        WizardFonts.UseLanguage(localizer.TryGetLanguage(out var startLanguage) ? startLanguage : null);
        Font = WizardFonts.Get(14, 400);
        TrySetWindowIcon();
        FormClosing += (_, e) =>
        {
            // Closing while the stick is being written asks first, and stops the write
            // properly if confirmed; it used to be refused without a word.
            if (_creatingUsb && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                AskToAbortCreation();
                return;
            }

            // While DexTor OS runs the launcher cannot go away - it owns the session - so
            // closing it puts it back in the notification area instead of doing nothing.
            if (_vmRunning && !_busy && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                MoveToTray();
                return;
            }

            e.Cancel = _busy || _vmRunning;
        };
        Resize += (_, _) =>
        {
            // Minimizing while DexTor OS runs goes to the notification area, every time,
            // not only right after the start.
            if (WindowState == FormWindowState.Minimized && _vmRunning && !_busy)
            {
                MoveToTray();
            }
        };

        var root = new GlassBackdrop { Name = "WizardRoot", Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = Padding.Empty };
        _root = root;

        _stepper = new StepperView { Name = "WizardStepper", Location = new Point(0, S(56)), Size = S(960, 84) };
        root.Controls.Add(BuildHeader());

        _contentHost = new Panel
        {
            Name = "WizardContentHost",
            Location = new Point(S(40), S(140)),
            Size = S(ContentWidth, 584 - 140 - 4),
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.Controls.Add(_contentHost);

        // The footer: the brand line - with the version, which is how builds are told
        // apart - the language list and the site link on the left, two buttons on the right.
        _actionBar = new Panel
        {
            Name = "WizardActionBar",
            Location = new Point(S(40), S(584)),
            Size = S(ContentWidth, 40),
            BackColor = Color.Transparent
        };
        // The left part only: overlapping the buttons, a transparent panel would paint over them.
        var footer = new Panel { Name = "DexTorBrandFooter", Location = Point.Empty, Size = S(560, 40), BackColor = Color.Transparent };
        _copyright = new WizardText(14, 600, 20, WizardDesign.TextSecondary)
        {
            Name = "DexTorBrandCopyright", Text = LauncherBranding.FooterText(localizer)
        };
        _languageSelector = LauncherBranding.CreateLanguageSelector(localizer, ChangeLanguage);
        _languageSelector.Size = S(170, 40);
        _languageSelector.Font = WizardFonts.Get(14, 600);
        _languageSelector.BackColor = WizardDesign.Secondary;
        _siteLink = new WizardText(14, 600, 20, WizardDesign.Link)
        {
            Name = "DexTorBrandSiteLink", Text = "dextoros.com", Cursor = Cursors.Hand, Tag = LauncherBranding.SiteUrl
        };
        _siteLink.Click += (_, _) => LauncherBranding.OpenSite();
        _siteLink.MouseEnter += (_, _) => _siteLink.ForeColor = WizardDesign.Text;
        _siteLink.MouseLeave += (_, _) => _siteLink.ForeColor = WizardDesign.Link;
        footer.Controls.Add(_copyright);
        footer.Controls.Add(_languageSelector);
        footer.Controls.Add(_siteLink);

        _backButton = CreateButton("← " + localizer.T("launcher.wizard.back"), ButtonKind.Secondary, 120);
        _backButton.Name = "WizardBackButton";
        _backButton.Click += async (_, _) =>
        {
            if (_secondaryAction is { } action)
            {
                await RunGuardedAsync(action);
            }
            else if (_busy && _canCancel)
            {
                CancelCurrentOperation();
            }
            else
            {
                GoBack();
            }
        };
        _primaryButton = CreateButton(localizer.T("launcher.primary_action_continue"), ButtonKind.Go, 200);
        _primaryButton.Name = "WizardPrimaryButton";
        _primaryButton.Trailing = "→";
        // An async event handler is an async void: an escaping exception would terminate
        // the process instead of reaching the UI, so it is caught and reported here.
        _primaryButton.Click += async (_, _) => await RunGuardedAsync(HandlePrimaryAsync);
        _closeButton = CreateButton(localizer.T("launcher.close"), ButtonKind.Secondary, 120);
        _closeButton.Name = "WizardCloseButton";
        _closeButton.Click += (_, _) =>
        {
            if (_creatingUsb)
            {
                AskToAbortCreation();
                return;
            }

            if (_launching)
            {
                // DexTor OS keeps starting; the launcher waits in the notification area.
                MoveToTray();
                return;
            }

            Close();
        };
        _actionBar.Controls.Add(_backButton);
        _actionBar.Controls.Add(_closeButton);
        _actionBar.Controls.Add(_primaryButton);
        _actionBar.Controls.Add(footer);
        _actionBar.Layout += (_, _) => LayoutActionBar();
        root.Controls.Add(_actionBar);

        _usbRefreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _usbRefreshTimer.Tick += async (_, _) => await RunGuardedAsync(RefreshUsbCandidatesAsync);
        _vmStateTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _vmStateTimer.Tick += async (_, _) =>
        {
            await CheckActiveVmUsbNowAsync();
            ObserveVmState();
            ObserveVmPause();
        };

        Controls.Add(root);
        ShowIntro();
    }

    internal LauncherWizardStep CurrentStep => _step;

    internal int UsbRefreshIntervalMilliseconds => _usbRefreshTimer.Interval;

    internal Task RefreshUsbNowAsync() => RefreshUsbCandidatesAsync();

    internal Task AdvanceAsync() => HandlePrimaryAsync();

    internal Task EjectAsync() => EjectUsbAsync();

    internal bool EjectOffered => _ejectAvailable;

    internal string? PageDetailText => _pageDetail?.Text;

    /// <summary>True while the USB step is still waiting for its first answer from Windows.</summary>
    internal bool UsbSearching { get; private set; }

    /// <summary>True after "Create the USB drive only": nothing will be started from Windows.</summary>
    internal bool UsbOnly => _usbOnly;

    /// <summary>
    /// Runs an async handler and reports a failure instead of letting it escape an
    /// <c>async void</c> event and take the process down.
    /// </summary>
    private async Task RunGuardedAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (_pageStatus is not null && _pageDetail is not null)
            {
                _pageStatus.Text = _localizer.T("launcher.wizard.vm.stop_failed");
                _pageStatus.ForeColor = ErrorColor;
                _pageDetail.Text = exception.Message;
            }
        }
    }

    // ------------------------------------------------------------------ chrome

    private Panel BuildHeader()
    {
        var header = new Panel
        {
            Name = "WizardHeader",
            Location = Point.Empty,
            Size = S(960, 140),
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };

        // The wordmark as the design sets it (see Generate-ThemeAssets.ps1), 149.6 x 34.4.
        var logo = new PictureBox
        {
            Name = "WizardLogo",
            Image = LoadEmbeddedBitmap("DexTor.Launcher.Assets.wordmark.png") ?? LoadEmbeddedBitmap("DexTor.Launcher.Assets.logo.png"),
            SizeMode = PictureBoxSizeMode.StretchImage,
            Bounds = new Rectangle(S(40.8f), S(16.5f), S(149.6f), S(34.4f)),
            BackColor = Color.Transparent,
            Margin = Padding.Empty
        };
        header.Controls.Add(logo);
        // One quiet badge beside the wordmark: what the build is.
        var beta = new HeaderBadge { Name = "WizardBetaBadge", Text = _localizer.T("launcher.wizard.badge_beta"), Location = new Point(S(203.2f), S(20.8f)) };
        header.Controls.Add(beta);

        foreach (var step in Steps)
        {
            var glyph = new StepCircle { Name = $"WizardStepGlyph{step}", Text = ((int)step + 1).ToString(CultureInfo.InvariantCulture) };
            var label = new WizardText(14, 600, 20, WizardDesign.TextSecondary) { Name = $"WizardStepLabel{step}", Text = _localizer.T(StepLabelKey(step)) };
            _stepper.Add(glyph, label);
            _stepGlyphs[step] = glyph;
            _stepLabels[step] = label;
        }

        header.Controls.Add(_stepper);
        return header;
    }

    private void LayoutActionBar()
    {
        // Every footer text sits on one baseline, centred in the 40 px of the buttons.
        var line = (S(40) - S(20)) / 2;
        _copyright.Location = new Point(0, line);
        _languageSelector.Location = new Point(_copyright.Right + S(16), 0);
        _siteLink.Location = new Point(_languageSelector.Right + S(16), line);
        if (_siteLink.Parent is { } footer) footer.Width = _siteLink.Right + S(2);
        var right = _actionBar.Width;
        _primaryButton.Location = new Point(right - _primaryButton.Width, 0);
        right = _primaryButton.Visible ? _primaryButton.Left - S(12) : right;
        foreach (var secondary in new[] { _closeButton, _backButton })
        {
            if (!secondary.Visible) continue;
            secondary.Location = new Point(right - secondary.Width, 0);
            right = secondary.Left - S(12);
        }

        // The brand line carries the version, so a long secondary action ("Cancel download")
        // can reach the site link; then the link steps aside rather than run under it.
        var linkFits = _siteLink.Right + S(16) <= right + S(12);
        if (_siteLink.Visible != linkFits) _siteLink.Visible = linkFits;
    }

    private enum PrimaryMark { Arrow, None, Play, Spinner }

    /// <summary>
    /// The two buttons. The secondary one is "← Back" where there is a way back, "Close"
    /// otherwise, or a named action of the page; the primary one carries the go arrow, the
    /// play mark, a waiting ring or nothing.
    /// </summary>
    private void ConfigureActions(
        bool showBack,
        string primaryText,
        bool primaryEnabled,
        PrimaryMark mark = PrimaryMark.Arrow,
        string? secondaryText = null,
        Func<Task>? secondaryAction = null,
        bool showSecondary = true,
        bool showPrimary = true)
    {
        _secondaryAction = secondaryAction;
        if (secondaryText is not null)
        {
            _backButton.Leading = null;
            _backButton.Text = secondaryText;
            _backButton.Visible = showSecondary;
            _closeButton.Visible = false;
        }
        else
        {
            _backButton.Leading = null;
            _backButton.Text = "← " + _localizer.T("launcher.wizard.back");
            _backButton.Visible = showBack && showSecondary;
            _closeButton.Visible = !showBack && showSecondary;
        }

        _backButton.Enabled = _backButton.Visible && (!_busy || _canCancel || secondaryAction is not null);
        _primaryButton.Visible = showPrimary;
        _primaryButton.Trailing = mark == PrimaryMark.Arrow ? "→" : null;
        _primaryButton.LeadingIcon = mark == PrimaryMark.Play ? WizardIcon.Play : WizardIcon.None;
        _primaryButton.Spinner = mark == PrimaryMark.Spinner;
        _primaryButton.Text = primaryText;
        _primaryButton.FitWidth();
        _primaryButton.Enabled = primaryEnabled && !_busy;
        _closeButton.Enabled = !_busy;
        LayoutActionBar();
        _primaryButton.Invalidate();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _backButton.Enabled = busy ? _canCancel || _secondaryAction is not null : true;
        _primaryButton.Enabled = !busy;
        _closeButton.Enabled = !busy || _launching || (_creatingUsb && !_abortRequested && _dependencies.CancelCreation is not null);
        if (!busy && _pendingLanguage is { } language)
        {
            _pendingLanguage = null;
            if (IsHandleCreated) BeginInvoke(() => ChangeLanguage(language));
            else ChangeLanguage(language);
        }
        // Busy state is communicated by the in-page progress bar. A system wait
        // cursor is visually noisy and can render as disconnected white shapes.
        UseWaitCursor = false;
    }

    private void SetStep(LauncherWizardStep step, bool failed = false)
    {
        _step = step;
        _failedStep = failed ? step : null;
        if (step == LauncherWizardStep.Usb)
        {
            _usbRefreshTimer.Start();
        }
        else
        {
            _usbRefreshTimer.Stop();
        }

        for (var i = 0; i < Steps.Length; i++)
        {
            var candidate = Steps[i];
            var glyph = _stepGlyphs[candidate];
            var label = _stepLabels[candidate];
            // The last step is done, not current, once the stick has been created.
            var done = i < (int)step || (step == LauncherWizardStep.Complete && _creationSucceeded);
            var isFailed = failed && candidate == step;
            glyph.Text = done ? "✓" : isFailed ? "✕" : (i + 1).ToString(CultureInfo.InvariantCulture);
            glyph.State = isFailed ? StepCircle.Kind.Failed : done ? StepCircle.Kind.Done : candidate == step ? StepCircle.Kind.Current : StepCircle.Kind.Pending;
            label.ForeColor = done || candidate == step ? WizardDesign.Text : WizardDesign.TextSecondary;
        }

        _stepper.Invalidate();
    }

    // ------------------------------------------------------------------ step 1

    private void ShowIntro()
    {
        SetStep(LauncherWizardStep.Intro);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.intro.title"),
            _localizer.T("launcher.wizard.intro.description"));
        var card = CreateInfoPanel();
        card.Name = "WizardIntroOverview";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.intro.overview")));
        card.Controls.Add(CreateIntroRow("pc", WizardIcon.Monitor, "item_pc", "detail_pc", LauncherWizardStep.HostCheck));
        card.Controls.Add(CreateIntroRow("image", WizardIcon.Download, "item_image", "detail_image", LauncherWizardStep.Image));
        card.Controls.Add(CreateIntroRow("usb", WizardIcon.Usb, "item_usb", "detail_usb", LauncherWizardStep.Usb));
        var notice = CreateNotice("WizardEraseNotice", Tone.Warning, WizardIcon.ShieldCheck, _localizer.T("launcher.wizard.intro.erase_notice"));
        card.Controls.Add(notice);
        card.Controls.Add(new FactStrip(_localizer.T("launcher.wizard.intro.needs"),
        [
            (WizardIcon.Usb, _localizer.T("launcher.wizard.intro.need_usb"), _localizer.T("launcher.wizard.intro.need_usb_caption")),
            (WizardIcon.Download, _localizer.T("launcher.wizard.intro.need_download"), _localizer.T("launcher.wizard.intro.need_download_caption")),
            (WizardIcon.Clock, _localizer.T("launcher.wizard.intro.need_time"), _localizer.T("launcher.wizard.intro.need_time_caption"))
        ])
        {
            Name = "WizardIntroNeeds", Width = S(RowWidth), Margin = S(0, 12, 0, 0)
        });
        AddToPage(panel, card);
        ConfigureActions(false, _localizer.T("launcher.primary_action_continue"), true);
    }

    /// <summary>
    /// One row of "what the wizard will do". The step it belongs to is named for screen
    /// readers; the design shows no number.
    /// </summary>
    private WizardRow CreateIntroRow(string name, WizardIcon icon, string title, string detail, LauncherWizardStep step)
    {
        var row = CreateRow("WizardIntroRow_" + name, icon,
            _localizer.T("launcher.wizard.intro." + title),
            _localizer.T("launcher.wizard.intro." + detail));
        row.AccessibleDescription = _localizer.T(StepLabelKey(step));
        row.Dense = true;
        row.Relayout();
        return row;
    }

    // ------------------------------------------------------------------ step 2

    /// <summary>The checks the design lists, in its order, with their icons.</summary>
    private static readonly (PreflightRequirementKind Kind, string Name, WizardIcon Icon, string TitleKey)[] HostChecks =
    [
        (PreflightRequirementKind.OperatingSystem, "windows", WizardIcon.Windows, "launcher.wizard.host.item_windows"),
        (PreflightRequirementKind.Memory, "memory", WizardIcon.Memory, "launcher.wizard.host.item_memory"),
        (PreflightRequirementKind.DiskSpace, "disk", WizardIcon.Drive, "launcher.wizard.host.item_disk"),
        (PreflightRequirementKind.HostVirtualization, "virtualization", WizardIcon.Layers, "launcher.wizard.host.item_virtualization")
    ];

    private readonly List<WizardRow> _hostRows = [];

    /// <summary>Counts host checks, so the answer to one the operator walked away from is dropped.</summary>
    private int _hostCheckRun;

    private async Task ShowHostCheckAsync()
    {
        _existingUsbVerified = false;
        _restartPending = false;
        SetStep(LauncherWizardStep.HostCheck);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.host.title"),
            _localizer.T("launcher.wizard.host.description"));
        panel.Scrollable = false;
        var card = CreateInfoPanel();
        card.Name = "WizardHostChecking";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.host.checking_caption")));
        _hostRows.Clear();
        for (var index = 0; index < HostChecks.Length; index++)
        {
            var row = CreateRow("WizardHostRow_" + HostChecks[index].Name, HostChecks[index].Icon, _localizer.T(HostChecks[index].TitleKey), null);
            row.SetTrailing(index == 0
                ? new RowStatus(_localizer.T("launcher.wizard.host.status_checking"), RowStatus.Kind.Checking)
                : new RowStatus(_localizer.T("launcher.wizard.host.status_waiting"), RowStatus.Kind.Pending));
            _hostRows.Add(row);
            card.Controls.Add(row);
        }

        // The status and detail of a running check: an existing DexTor stick is verified here.
        _pageStatus = new WizardText(14, 600, 20, WizardDesign.Text) { Name = "WizardHostStatus", Visible = false, WrapWidth = S(RowWidth) };
        _pageDetail = new WizardText(14, 400, 20, WizardDesign.TextSecondary) { Name = "WizardHostDetail", Visible = false, WrapWidth = S(RowWidth) };
        card.Controls.Add(_pageStatus);
        card.Controls.Add(_pageDetail);
        _pageProgress = new WizardProgressBar
        {
            Name = "WizardProgressBar",
            Width = S(RowWidth),
            Maximum = 1000,
            Margin = S(0, 4, 0, 0)
        };
        card.Controls.Add(_pageProgress);
        AddToPage(panel, card);
        var check = ++_hostCheckRun;
        ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), false,
            secondaryText: "← " + _localizer.T("launcher.wizard.back"),
            secondaryAction: () =>
            {
                // Going back abandons this check; its answer is ignored when it arrives.
                _hostCheckRun++;
                SetBusy(false);
                ShowIntro();
                return Task.CompletedTask;
            });
        SetBusy(true);
        var creep = StartCreep(_pageProgress, 0.4);
        try
        {
            var environment = await _dependencies.RefreshEnvironment();
            creep.Stop();
            if (check != _hostCheckRun) return;
            _environment = environment;
            await RevealHostRowsAsync(_environment.Preflight, () => check == _hostCheckRun);
            if (check != _hostCheckRun) return;
            if (CanCheckExistingUsb(_environment))
            {
                _pageStatus.Visible = true;
                _pageDetail.Visible = true;
                _pageStatus.Text = _localizer.T("launcher.wizard.host.usb_checking");
                _pageDetail.Text = _localizer.T("launcher.wizard.host.usb_checking_details");
                _pageProgress.IsIndeterminate = true;
                var usbProgress = new Progress<DexTorUsbVerificationProgress>(UpdateExistingUsbProgress);
                var verification = await _dependencies.VerifyDexTorUsb(
                    _environment.Context.DextorRootPath!,
                    usbProgress,
                    CancellationToken.None);
                if (verification.IsReady)
                {
                    _existingUsbVerified = true;
                    await ShowExistingUsbReadyAsync();
                    return;
                }
            }

            RenderHostResult(panel, _environment.Preflight);
        }
        catch (Exception exception) when (check == _hostCheckRun)
        {
            _pageStatus.Visible = true;
            _pageStatus.Text = _localizer.T("launcher.wizard.host.failed", Args(("error", exception.Message)));
            _pageStatus.ForeColor = ErrorColor;
            ConfigureActions(true, _localizer.T("launcher.wizard.host.check_again"), true, PrimaryMark.None);
        }
        finally
        {
            creep.Stop();
            creep.Dispose();
            if (check == _hostCheckRun) SetBusy(false);
        }
    }

    /// <summary>
    /// Moves a bar slowly towards <paramref name="ceiling"/> while an answer that reports no
    /// progress of its own is awaited - it moves, but never claims to be nearly done.
    /// </summary>
    private static System.Windows.Forms.Timer StartCreep(WizardProgressBar bar, double ceiling)
    {
        var timer = new System.Windows.Forms.Timer { Interval = 120 };
        timer.Tick += (_, _) =>
        {
            var target = (int)(bar.Maximum * ceiling);
            if (bar.IsDisposed || bar.Value >= target) return;
            bar.Value += Math.Max(1, (target - bar.Value) / 12);
        };
        timer.Start();
        return timer;
    }

    /// <summary>
    /// Fills in the checks one after another, the way the design shows them: the one being
    /// read with a ring, the ones after it waiting. Without a visible window it is immediate.
    /// </summary>
    private async Task RevealHostRowsAsync(PreflightReport report, Func<bool> current)
    {
        var animate = CanAnimateTransitions;
        var rows = BuildFriendlyHostRows(report).ToDictionary(row => row.Name, row => row);
        for (var index = 0; index < _hostRows.Count && index < HostChecks.Length; index++)
        {
            if (!current()) return;
            if (rows.TryGetValue(HostChecks[index].Name, out var found))
            {
                _hostRows[index].SetTrailing(CreateHostStatus(found.State, found.Value));
            }
            else
            {
                _hostRows[index].SetTrailing(CreateHostStatus(PreflightRequirementState.Ok, null));
            }

            if (index + 1 < _hostRows.Count)
            {
                _hostRows[index + 1].SetTrailing(new RowStatus(_localizer.T("launcher.wizard.host.status_checking"), RowStatus.Kind.Checking));
            }

            if (_pageProgress is not null)
            {
                _pageProgress.Value = Math.Max(_pageProgress.Value, (int)(_pageProgress.Maximum * (index + 1) / (double)_hostRows.Count));
            }

            if (animate) await Task.Delay(220);
        }

        if (animate) await Task.Delay(260);
    }

    private RowStatus CreateHostStatus(PreflightRequirementState state, string? value) => new(
        value ?? _localizer.T(state == PreflightRequirementState.Ok ? "launcher.wizard.host.value_ok" : "launcher.wizard.host.value_problem"),
        state switch
        {
            PreflightRequirementState.Ok => RowStatus.Kind.Ok,
            PreflightRequirementState.Warning => RowStatus.Kind.Warning,
            _ => RowStatus.Kind.Error
        });

    private static bool CanCheckExistingUsb(LauncherWizardEnvironment environment)
    {
        return environment.Context.IsDexTorUsb
            && !string.IsNullOrWhiteSpace(environment.Context.DextorRootPath)
            && environment.Context.IsWhpxEnabled
            && !environment.Context.NeedsReboot
            && environment.Preflight.CanContinueWithWarnings
            && !environment.Preflight.Requirements.Any(requirement =>
                requirement.State == PreflightRequirementState.Blocker
                || requirement.Kind == PreflightRequirementKind.HostVirtualization
                    && requirement.State == PreflightRequirementState.Warning);
    }

    private void UpdateExistingUsbProgress(DexTorUsbVerificationProgress progress)
    {
        if (_step != LauncherWizardStep.HostCheck || _pageProgress is null || _pageDetail is null) return;
        if (progress.TotalBytes <= 0)
        {
            _pageProgress.IsIndeterminate = true;
            return;
        }

        var percent = (int)Math.Clamp(progress.BytesChecked * 100d / progress.TotalBytes, 0, 100);
        _pageProgress.IsIndeterminate = false;
        _pageProgress.Maximum = 100;
        _pageProgress.Value = percent;
        _pageDetail.Text = _localizer.T("launcher.wizard.host.usb_progress", Args(
            ("percent", percent.ToString(CultureInfo.InvariantCulture)),
            ("checked", FormatBytes(progress.BytesChecked)),
            ("total", FormatBytes(progress.TotalBytes))));
    }

    private void RenderHostResult(WizardPage panel, PreflightReport report)
    {
        BeginTransition(_contentHost);
        panel.Scrollable = true;
        ClearPage(panel);
        AddHeading(panel, _localizer.T("launcher.wizard.host.title"), _localizer.T("launcher.wizard.host.description"));
        var results = CreateInfoPanel();
        results.Name = "WizardHostResults";
        results.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.host.results")));
        var listed = BuildFriendlyHostRows(report).ToList();
        foreach (var row in listed)
        {
            var line = CreateRow("WizardHostRow_" + row.Name, row.Icon, _localizer.T(row.TitleKey), null);
            line.SetTrailing(CreateHostStatus(row.State, row.Value));
            results.Controls.Add(line);
        }

        var hasBlocker = report.Requirements.Any(r => r.State == PreflightRequirementState.Blocker);
        var hasWhpxWarning = report.Requirements.Any(r =>
            r.Kind == PreflightRequirementKind.HostVirtualization && r.State == PreflightRequirementState.Warning);
        GlassNotice summary;
        if (hasBlocker)
        {
            summary = CreateNotice("WizardHostSummary", Tone.Error, WizardIcon.Alert, _localizer.T("launcher.wizard.host.blocked"));
        }
        else if (_environment?.Context.NeedsReboot == true)
        {
            summary = CreateNotice("WizardHostSummary", Tone.Warning, WizardIcon.Restart, _localizer.T("launcher.wizard.host.restart_needed"));
        }
        else if (hasWhpxWarning)
        {
            // Without virtualization the stick can still be made; the operator may also have
            // the wizard switch the Windows component on.
            summary = CreateNotice("WizardHostSummary", Tone.Warning, WizardIcon.Warning, _localizer.T("launcher.wizard.host.virtualization_off"));
            summary.Action = _localizer.T("launcher.wizard.host.enable_virtualization");
            summary.ActionClicked += async (_, _) => await RunGuardedAsync(PreparePcAsync);
        }
        else
        {
            summary = CreateNotice("WizardHostSummary", Tone.Success, WizardIcon.Check, _localizer.T("launcher.wizard.host.ready"));
        }

        results.Controls.Add(summary);
        AddToPage(panel, results);
        if (hasBlocker)
        {
            ConfigureActions(true, _localizer.T("launcher.wizard.host.check_again"), true, PrimaryMark.None);
        }
        else if (_environment?.Context.NeedsReboot == true)
        {
            ConfigureActions(true, _localizer.T("launcher.primary_action_restart_required"), true, PrimaryMark.None);
        }
        else if (hasWhpxWarning)
        {
            ConfigureActions(true, _localizer.T("launcher.wizard.host.usb_only"), true,
                secondaryText: _localizer.T("launcher.wizard.host.check_again"), secondaryAction: ShowHostCheckAsync);
        }
        else
        {
            ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), true);
        }
    }

    /// <summary>
    /// One line per thing the operator would recognise, in the design's order: Windows,
    /// memory, free space, virtualization. The connection and application support are shown
    /// only when they fail - the runtime ships inside the launcher.
    /// </summary>
    private IEnumerable<(string Name, PreflightRequirementState State, WizardIcon Icon, string TitleKey, string? Value)> BuildFriendlyHostRows(PreflightReport report)
    {
        var compatibility = report.Requirements.Where(r =>
            r.Kind is PreflightRequirementKind.OperatingSystem or PreflightRequirementKind.ProcessorArchitecture).ToArray();
        if (compatibility.Length > 0)
        {
            var windows = compatibility.FirstOrDefault(r => r.Kind == PreflightRequirementKind.OperatingSystem);
            yield return ("windows", compatibility.MaxBy(r => (int)r.State)!.State, WizardIcon.Windows, "launcher.wizard.host.item_windows",
                windows is null ? null : DescribeWindows(windows));
        }

        var order = new[]
        {
            (PreflightRequirementKind.Memory, "memory", WizardIcon.Memory, "launcher.wizard.host.item_memory"),
            (PreflightRequirementKind.DiskSpace, "disk", WizardIcon.Drive, "launcher.wizard.host.item_disk"),
            (PreflightRequirementKind.HostVirtualization, "virtualization", WizardIcon.Layers, "launcher.wizard.host.item_virtualization"),
            (PreflightRequirementKind.LiveImage, "connection", WizardIcon.Download, "launcher.wizard.host.item_connection"),
            (PreflightRequirementKind.Runtime, "runtime", WizardIcon.Monitor, "launcher.wizard.host.item_runtime")
        };
        foreach (var (kind, name, icon, key) in order)
        {
            foreach (var requirement in report.Requirements.Where(r => r.Kind == kind))
            {
                if (kind is PreflightRequirementKind.Runtime or PreflightRequirementKind.LiveImage && requirement.State == PreflightRequirementState.Ok) continue;
                yield return (name, requirement.State, icon, key, DescribeValue(requirement));
            }
        }
    }

    /// <summary>"Windows 11": the product line, without the edition, release and build.</summary>
    private string? DescribeWindows(PreflightRequirement windows)
    {
        var facts = windows.DescriptionArgs;
        if (facts is null || !facts.TryGetValue("product", out var product)) return null;
        var line = WindowsLine().Match(product);
        return _localizer.T("launcher.wizard.host.value_windows", Args(("product", line.Success ? line.Value : product)));
    }

    private string? DescribeValue(PreflightRequirement requirement)
    {
        long Bytes() => requirement.DescriptionArgs is { } facts && facts.TryGetValue("bytes", out var text)
            && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        return requirement.Kind switch
        {
            PreflightRequirementKind.Memory when Bytes() > 0 => FormatBytes(Bytes()),
            PreflightRequirementKind.DiskSpace when Bytes() > 0 => _localizer.T("launcher.wizard.host.value_disk", Args(
                ("size", FormatBytes(Bytes())),
                ("drive", requirement.DescriptionArgs!.TryGetValue("drive", out var drive) ? drive : "?"))),
            PreflightRequirementKind.HostVirtualization => _localizer.T(requirement.State == PreflightRequirementState.Ok
                ? "launcher.wizard.host.value_whpx"
                : "launcher.wizard.host.value_whpx_off"),
            PreflightRequirementKind.Runtime when requirement.DescriptionArgs?.TryGetValue("version", out var version) == true => ".NET " + version,
            _ => null
        };
    }

    /// <summary>"How to enable": the wizard switches the Windows component on, then asks for a restart.</summary>
    private async Task PreparePcAsync()
    {
        if (_busy) return;
        var notice = Controls.Find("WizardHostSummary", true).OfType<GlassNotice>().FirstOrDefault();
        if (notice is not null)
        {
            notice.Action = null;
            notice.Text = _localizer.T("launcher.prepare_progress.running");
            notice.Invalidate();
        }

        ConfigureActions(true, _localizer.T("launcher.wizard.host.usb_only"), false,
            secondaryText: _localizer.T("launcher.wizard.host.check_again"), secondaryAction: ShowHostCheckAsync);
        SetBusy(true);
        var result = await _dependencies.PreparePc();
        SetBusy(false);
        if (notice is not null)
        {
            notice.Kind = result.Success ? Tone.Warning : Tone.Error;
            notice.Icon = result.Success ? WizardIcon.Restart : WizardIcon.Alert;
            notice.Text = string.IsNullOrWhiteSpace(result.Detail) ? result.Title : result.Title + " " + result.Detail;
            notice.Invalidate();
        }

        if (result.Success)
        {
            _restartPending = true;
            ConfigureActions(true, _localizer.T("launcher.primary_action_restart_required"), true, PrimaryMark.None);
        }
        else
        {
            ConfigureActions(true, _localizer.T("launcher.wizard.host.usb_only"), true,
                secondaryText: _localizer.T("launcher.wizard.host.check_again"), secondaryAction: ShowHostCheckAsync);
        }
    }

    // ------------------------------------------------------------------ step 3

    private async Task ShowImageAsync()
    {
        _imageFailure = null;
        BuildImagePage();
        ConfigureActions(true, _localizer.T("launcher.image_setup.download"), false);
        SetBusy(true);
        _acceptImageProgress = true;
        try
        {
            var progress = new Progress<LiveImagePreparationProgress>(UpdateImageProgress);
            _imageInspection = await _dependencies.InspectImage(CancellationToken.None, progress);
        }
        finally
        {
            _acceptImageProgress = false;
            SetBusy(false);
        }

        if (_imageInspection is null || !_imageInspection.Success)
        {
            RenderImageInspectionFailed();
            return;
        }

        if (_imageInspection.IsReady)
        {
            _preparedImage = new LiveImagePreparationResult(
                true,
                _imageInspection.SourceUrl,
                _imageInspection.LocalImagePath,
                _imageInspection.SourceSha256,
                _imageInspection.SourceSizeBytes,
                SigningKey: _imageInspection.SigningKey);
            RenderImageReady();
            return;
        }

        RenderImageDownloadRequired();
    }

    private void BuildImagePage(bool failed = false)
    {
        SetStep(LauncherWizardStep.Image, failed);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.image.title"),
            _localizer.T("launcher.wizard.image.description"));
        _transferStage = null;
        _transferSamples.Clear();
        _smoothedSpeed = null;
        AddToPage(panel, CreateStatusCard(
            _localizer.T("launcher.wizard.image.caption"), WizardIcon.Download,
            _localizer.T("launcher.image_setup.checking"),
            _localizer.T("launcher.image_setup.checking_details"),
            transfer: true, progress: true));
        _pageProgress!.IsIndeterminate = true;
    }

    private void RenderImageInspectionFailed()
    {
        // A signature that does not hold says so; anything else - no answer from the release
        // index above all - reads as the download failing, which it is.
        var key = _imageInspection?.ErrorMessageKey == LiveImagePreparationService.SignatureFailedKey
            ? LiveImagePreparationService.SignatureFailedKey
            : LiveImagePreparationService.DownloadFailedKey;
        RenderImageFailure(new LiveImagePreparationResult(false, _imageInspection?.SourceUrl, null, null, 0,
            _imageInspection?.ErrorMessage, key));
    }

    private void RenderImageDownloadRequired()
    {
        BuildImagePage();
        _pageStatus!.Text = _localizer.T("launcher.image_setup.download_required");
        _pageStatus.ForeColor = WizardDesign.Text;
        _pageDetail!.Text = _localizer.T("launcher.image_setup.download_description");
        _pageProgress!.Visible = false;
        ConfigureActions(true, _localizer.T("launcher.image_setup.download"), true);
    }

    private async Task DownloadImageAsync()
    {
        if (_imageFailure is not null || _pageStatus is null || _pageDetail is null || _pageProgress is null)
        {
            RenderImageDownloadRequired();
        }

        _imageFailure = null;
        _pageStatus!.Text = _localizer.T("launcher.wizard.image.downloading_title");
        _pageStatus.ForeColor = WizardDesign.Text;
        _pageDetail!.Text = _localizer.T("launcher.wizard.image.downloading_detail");
        _pageProgress!.Visible = true;
        _pageProgress.IsIndeterminate = true;
        using var cancellation = new CancellationTokenSource();
        _activeOperationCancellation = cancellation;
        _canCancel = true;
        // The page says it is downloading; a greyed-out "Downloading..." button only said
        // it twice and looked like something to press. Cancelling is the one action.
        ConfigureActions(true, _localizer.T("launcher.wizard.image.downloading_button"), false, PrimaryMark.None,
            secondaryText: _localizer.T("launcher.wizard.image.cancel"),
            secondaryAction: () =>
            {
                CancelCurrentOperation();
                return Task.CompletedTask;
            },
            showPrimary: false);
        SetBusy(true);
        _acceptImageProgress = true;
        try
        {
            var progress = new Progress<LiveImagePreparationProgress>(UpdateImageProgress);
            _preparedImage = await _dependencies.PrepareImage(cancellation.Token, progress);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            _preparedImage = null;
        }
        finally
        {
            _acceptImageProgress = false;
            HideTransferStats();
            _canCancel = false;
            _activeOperationCancellation = null;
            SetBusy(false);
        }

        if (cancellation.IsCancellationRequested)
        {
            _pageStatus.Text = _localizer.T("launcher.wizard.image.cancelled");
            _pageStatus.ForeColor = WizardDesign.Text;
            _pageDetail.Text = _localizer.T("launcher.wizard.image.cancelled_description");
            _pageProgress.Visible = false;
            _preparedImage = null;
            ConfigureActions(true, _localizer.T("launcher.image_setup.download"), true);
            return;
        }

        if (_preparedImage?.Success == true)
        {
            RenderImageReady();
            return;
        }

        var failure = _preparedImage ?? new LiveImagePreparationResult(false, null, null, null, 0, null, LiveImagePreparationService.DownloadFailedKey);
        _preparedImage = null;
        RenderImageFailure(failure);
    }

    /// <summary>
    /// The ways the download can fail, each with its own block and next step: no connection
    /// (retry), a signature that does not hold or an image that failed its check (download
    /// again), no room (retry once space is freed). The step turns red in the stepper.
    /// </summary>
    private void RenderImageFailure(LiveImagePreparationResult failure)
    {
        _imageFailure = failure;
        SetStep(LauncherWizardStep.Image, failed: true);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.image.title"),
            _localizer.T("launcher.wizard.image.description"));
        var card = CreateInfoPanel();
        card.Name = "WizardStatusCard";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.image.caption")));
        string title, detail, action;
        var showDetails = true;
        switch (failure.ErrorMessageKey)
        {
            case LiveImagePreparationService.SignatureFailedKey:
                title = _localizer.T("launcher.wizard.image.failed_signature_title");
                detail = _localizer.T("launcher.wizard.image.failed_checksum_detail");
                action = _localizer.T("launcher.wizard.image.download_again");
                break;
            case LiveImagePreparationService.ChecksumFailedKey:
                title = _localizer.T("launcher.wizard.image.failed_checksum_title");
                detail = _localizer.T("launcher.wizard.image.failed_checksum_detail");
                action = _localizer.T("launcher.wizard.image.download_again");
                break;
            case LiveImagePreparationService.NoSpaceKey:
                var args = failure.ErrorMessageArgs ?? new Dictionary<string, string>();
                long Read(string key) => args.TryGetValue(key, out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
                title = _localizer.T("launcher.wizard.image.failed_space_title", Args(("drive", args.TryGetValue("drive", out var drive) ? drive : "?")));
                var needed = Read("needed_bytes");
                detail = _localizer.T("launcher.wizard.image.failed_space_detail", Args(
                    ("needed", FormatBytes(needed)),
                    ("available", FormatBytesLike(Read("available_bytes"), needed))));
                action = _localizer.T("launcher.wizard.image.retry");
                showDetails = false;
                break;
            default:
                title = _localizer.T("launcher.wizard.image.failed_download_title");
                detail = _localizer.T("launcher.wizard.image.failed_download_detail");
                action = _localizer.T("launcher.wizard.image.retry");
                break;
        }

        var block = new ErrorBlock(S(RowWidth), title, detail) { Name = "WizardImageError" };
        _pageStatus = block.TitleText;
        _pageDetail = block.DetailText;
        card.Controls.Add(block);
        if (showDetails && !string.IsNullOrWhiteSpace(failure.ErrorMessage))
        {
            AddDetails(card, [(_localizer.T("launcher.wizard.image.details_error"), failure.ErrorMessage!)], offset: 72);
        }

        AddToPage(panel, card);
        ConfigureActions(true, action, true, PrimaryMark.None);
    }

    private void RenderImageReady()
    {
        SetStep(LauncherWizardStep.Image);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.image.title"),
            _localizer.T("launcher.wizard.image.description"));
        var card = CreateInfoPanel();
        card.Name = "WizardStatusCard";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.image.verify_caption")));
        var image = _preparedImage!;
        var size = image.SourceSizeBytes > 0 ? image.SourceSizeBytes
            : !string.IsNullOrWhiteSpace(image.LocalImagePath) && File.Exists(image.LocalImagePath) ? new FileInfo(image.LocalImagePath).Length : 0;
        var downloaded = CreateRow("WizardImageDownloaded", WizardIcon.Download, ImageName(), null);
        downloaded.SetTrailing(new RowStatus(_localizer.T("launcher.wizard.image.value_downloaded", Args(("size", size > 0 ? FormatBytes(size) : "—"))), RowStatus.Kind.Ok));
        card.Controls.Add(downloaded);
        // An image only gets here once the release key's signature and the SHA-256 both held.
        var signature = CreateRow("WizardImageSignature", WizardIcon.ShieldCheck, _localizer.T("launcher.wizard.image.signature"), null);
        signature.SetTrailing(new RowStatus(_localizer.T("launcher.wizard.image.value_verified"), RowStatus.Kind.Ok));
        card.Controls.Add(signature);
        var checksum = CreateRow("WizardImageChecksum", WizardIcon.Hash, _localizer.T("launcher.wizard.image.checksum"), null);
        checksum.SetTrailing(new RowStatus(_localizer.T("launcher.wizard.image.value_matches"), RowStatus.Kind.Ok));
        card.Controls.Add(checksum);
        _pageStatus = downloaded.TitleText;
        _pageDetail = downloaded.DetailText;
        var details = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(image.LocalImagePath))
        {
            details.Add((_localizer.T("launcher.wizard.image.details_file"),
                Path.GetFileName(image.LocalImagePath) + (size > 0 ? " · " + FormatBytes(size) : string.Empty)));
        }

        if (!string.IsNullOrWhiteSpace(image.SourceSha256))
        {
            details.Add(("SHA-256", GroupHex(image.SourceSha256)));
        }

        if (!string.IsNullOrWhiteSpace(image.SigningKey))
        {
            details.Add((_localizer.T("launcher.wizard.image.details_key"), image.SigningKey));
        }

        // Where the image is kept, so it can be seen that it is not in %TEMP%.
        if (Path.GetDirectoryName(image.LocalImagePath) is { Length: > 0 } folder)
        {
            details.Add((_localizer.T("launcher.wizard.image.details_folder"), folder));
        }

        if (details.Count > 0) AddDetails(card, details.ToArray(), offset: 0);
        AddToPage(panel, card);
        ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), true);
        // The next step is choosing a drive. Asking Windows now means the list is
        // usually ready by the time the operator gets there.
        _usbPrefetch = (DateTime.UtcNow, _dependencies.InspectUsbDrives(CancellationToken.None));
    }

    /// <summary>"DexTor OS 1.0": the version read from the image's name or address when it carries one.</summary>
    private string ImageName()
    {
        var source = $"{Path.GetFileName(_preparedImage?.LocalImagePath ?? _lastCreateImage ?? string.Empty)} {_preparedImage?.SourceUrl}";
        var version = VersionNumber().Match(source);
        return version.Success ? "DexTor OS " + version.Groups[1].Value : "DexTor OS";
    }

    private string? _lastCreateImage;

    [GeneratedRegex(@"Windows\s+\d+", RegexOptions.CultureInvariant)]
    private static partial Regex WindowsLine();

    [GeneratedRegex(@"(?<![\d.])(\d+\.\d+(?:\.\d+)?)(?!\.?\d)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionNumber();

    private static string GroupHex(string hex)
    {
        var clean = hex.Trim().ToLowerInvariant();
        return string.Join(' ', Enumerable.Range(0, (clean.Length + 3) / 4).Select(i => clean.Substring(i * 4, Math.Min(4, clean.Length - i * 4))));
    }

    /// <summary>"▸ Details" under a block, opening the technical details.</summary>
    private void AddDetails(WizardInfoPanel card, (string Name, string Value)[] lines, float offset)
    {
        var toggle = new DetailsToggle(_localizer.T("launcher.wizard.image.details")) { Name = "WizardDetailsToggle", Margin = S(offset, 4, 0, 0) };
        var box = new DetailsBox(S(RowWidth)) { Name = "WizardDetails", Visible = false, Margin = S(0, 8, 0, 0) };
        box.SetLines(lines);
        toggle.Target = box;
        card.Controls.Add(toggle);
        card.Controls.Add(box);
    }

    private void UpdateImageProgress(LiveImagePreparationProgress update)
    {
        if (!_acceptImageProgress || _pageStatus is null || _pageDetail is null || _pageProgress is null)
        {
            return;
        }

        var isDownload = update.Stage is LiveImagePreparationService.StageDownload or QemuRuntimePreparationService.StageDownload;
        if (!isDownload) _pageStatus.Text = _localizer.T(update.Stage);
        if (update.TotalBytes is { } total && total > 0)
        {
            var percent = (int)Math.Clamp(update.BytesDownloaded * 100d / total, 0, 100);
            _pageProgress.IsIndeterminate = false;
            _pageProgress.Maximum = 1000;
            _pageProgress.Value = (int)Math.Clamp(update.BytesDownloaded * 1000d / total, 0, 1000);
            var speed = ObserveTransfer(update.Stage, update.BytesDownloaded) ?? update.BytesPerSecond;
            TimeSpan? eta = speed is > 0
                ? TimeSpan.FromSeconds(Math.Max(0, total - update.BytesDownloaded) / speed.Value)
                : update.EstimatedRemaining;
            if (isDownload)
            {
                var summary = _localizer.T("launcher.wizard.image.progress", Args(
                    ("percent", percent.ToString(CultureInfo.InvariantCulture)),
                    ("downloaded", FormatBytes(update.BytesDownloaded)),
                    ("total", FormatBytes(total)),
                    ("speed", speed is { } bytesPerSecond ? FormatBytesPerSecond(bytesPerSecond) : "—"),
                    ("eta", eta is { } left ? FormatDuration(left) : "—")));
                ShowTransferStats(summary, speed, update.BytesDownloaded, total, eta, percent);
                _pageStatus.Text = _localizer.T("launcher.wizard.image.downloading_title");
                _pageDetail.Text = _localizer.T("launcher.wizard.image.downloading_detail");
            }
            else
            {
                HideTransferStats();
                _pageDetail.Text = _localizer.T("launcher.wizard.image.verify_progress", Args(
                    ("percent", percent.ToString(CultureInfo.InvariantCulture)),
                    ("checked", FormatBytes(update.BytesDownloaded)),
                    ("total", FormatBytes(total))));
            }
        }
        else
        {
            HideTransferStats();
            _pageProgress.IsIndeterminate = true;
            if (isDownload) _pageStatus.Text = _localizer.T("launcher.wizard.image.downloading_title");
            _pageDetail.Text = _localizer.T("launcher.wizard.image.downloading_detail");
        }
    }

    /// <summary>
    /// Speed over the last few seconds, not since the start: a download that stalls
    /// must show it within seconds, and one that recovers must not stay pessimistic.
    /// </summary>
    private double? ObserveTransfer(string stage, long bytes)
    {
        var now = _transferClock.Elapsed.TotalSeconds;
        if (!string.Equals(stage, _transferStage, StringComparison.Ordinal)
            || (_transferSamples.Count > 0 && bytes < _transferSamples.Last().Bytes))
        {
            _transferStage = stage;
            _transferSamples.Clear();
            _smoothedSpeed = null;
            _transferStats?.ResetHistory();
        }

        _transferSamples.Enqueue((now, bytes));
        while (_transferSamples.Count > 2 && now - _transferSamples.Peek().Seconds > 3)
        {
            _transferSamples.Dequeue();
        }

        var (firstSeconds, firstBytes) = _transferSamples.Peek();
        var window = now - firstSeconds;
        if (window < 0.4) return _smoothedSpeed;
        var instant = (bytes - firstBytes) / window;
        _smoothedSpeed = _smoothedSpeed is { } previous ? previous + (instant - previous) * 0.35 : instant;
        return _smoothedSpeed;
    }

    private void ShowTransferStats(string summary, double? bytesPerSecond, long done, long total, TimeSpan? eta, int percent) =>
        ShowTransferStats(
            summary,
            [
                (_localizer.T("launcher.wizard.image.stat_speed"), bytesPerSecond is { } speed ? FormatBytesPerSecond(speed) : "—"),
                (_localizer.T("launcher.wizard.image.stat_downloaded"), $"{FormatBytes(done)} / {FormatBytes(total)}"),
                (_localizer.T("launcher.wizard.image.stat_remaining"), eta is { } left ? FormatDuration(left) : "—")
            ],
            bytesPerSecond,
            percent);

    private void ShowTransferStats(string summary, (string Caption, string Value)[] columns, double? bytesPerSecond, int percent)
    {
        if (_transferStats is null) return;
        _transferStats.Text = summary;
        _transferStats.AccessibleDescription = summary;
        _transferStats.SetColumns(columns, percent.ToString(CultureInfo.InvariantCulture) + "%");
        if (bytesPerSecond is { } sample) _transferStats.AddSample(sample);
        if (!_transferStats.Visible)
        {
            _transferStats.Visible = true;
        }
    }

    private void HideTransferStats()
    {
        if (_transferStats is { Visible: true }) _transferStats.Visible = false;
    }

    private void CancelCurrentOperation()
    {
        if (!_canCancel || _activeOperationCancellation is null)
        {
            return;
        }

        _backButton.Enabled = false;
        _backButton.Text = _localizer.T("launcher.wizard.image.cancelling");
        _activeOperationCancellation.Cancel();
    }

    // ------------------------------------------------------------------ step 4

    private async Task ShowUsbAsync()
    {
        _usbDiscoveryCompleted = false;
        _usbSnapshots = [];
        BuildUsbPage();
        await RefreshUsbCandidatesAsync();
    }

    /// <summary>
    /// Lays out the USB step. It starts in a visible "searching" state: an empty list and
    /// empty fields read as a frozen window while Windows is still being asked.
    /// </summary>
    private void BuildUsbPage(string? selectedIdentity = null, string? selectedRoot = null)
    {
        _usbList = null;
        _selectedUsb = null;
        SetStep(LauncherWizardStep.Usb);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.usb.title"),
            _localizer.T("launcher.wizard.usb.description"));
        UsbSearching = !_usbDiscoveryCompleted;

        // "Drive" with the refresh action beside it, as in the design.
        var header = new Panel { Name = "WizardUsbListHeader", Size = S(ContentWidth, 32), BackColor = Color.Transparent };
        var caption = CreateCaption(_localizer.T("launcher.wizard.usb.caption"));
        caption.Location = new Point(0, S(8));
        header.Controls.Add(caption);
        var refresh = CreateButton(_localizer.T("launcher.wizard.usb.refresh"), ButtonKind.Outline, 0);
        refresh.Name = "WizardUsbRefresh";
        refresh.Height = S(32);
        refresh.LeadingIcon = WizardIcon.Refresh;
        refresh.MinimumWidth = 1;
        refresh.FitWidth();
        refresh.Location = new Point(header.Width - refresh.Width, 0);
        refresh.Click += async (_, _) => await RunGuardedAsync(RefreshUsbCandidatesAsync);
        header.Controls.Add(refresh);
        _usbListHeader = header;
        AddToPage(panel, header, 16);

        // Searching, and nothing found: one card with the stick's icon in the middle.
        _usbSearch = CreateInfoPanel();
        _usbSearch.Name = "WizardUsbSearching";
        _usbSearch.Controls.Add(new EmptyState(S(RowWidth),
            _localizer.T("launcher.wizard.usb.searching"),
            _localizer.T("launcher.wizard.usb.searching_details"),
            null) { Name = "WizardUsbEmpty" });
        AddToPage(panel, _usbSearch, 12);

        _usbList = new UsbDriveField
        {
            Name = "WizardUsbList",
            Width = S(ContentWidth),
            Visible = false,
            Margin = S(0, 8, 0, 0),
            Describe = item => item is UsbListItem usb ? DescribeUsbField(usb.Snapshot) : (item.ToString() ?? string.Empty, WizardIcon.Usb, null)
        };
        _usbList.SelectedIndexChanged += (_, _) => UpdateSelectedUsbDetails();
        panel.Controls.Add(_usbList);

        _usbInfo = CreateInfoPanel();
        _usbInfo.Name = "WizardUsbInfo";
        _usbInfo.Visible = false;
        _usbInfo.Margin = S(0, 12, 0, 0);
        _usbInfo.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.usb.check_caption")));
        var statusRow = new UsbCheckRow(S(RowWidth)) { Name = "WizardUsbStatusRow" };
        _usbStatus = statusRow.Title;
        _usbStatusMark = statusRow.Mark;
        _usbFacts = statusRow.Facts;
        _usbDetails = statusRow.Details;
        _usbInfo.Controls.Add(statusRow);
        _usbEraseNotice = CreateNotice("WizardUsbEraseNotice", Tone.Warning, WizardIcon.Warning, _localizer.T("launcher.wizard.usb.erase_description"));
        _usbInfo.Controls.Add(_usbEraseNotice);
        panel.Controls.Add(_usbInfo);
        ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), false);
        if (_usbDiscoveryCompleted)
        {
            PopulateUsbList(selectedIdentity, selectedRoot);
        }
    }

    private async Task RefreshUsbCandidatesAsync()
    {
        if (_step != LauncherWizardStep.Usb || _busy || _usbList is null || _usbRefreshInProgress)
        {
            return;
        }

        _usbRefreshInProgress = true;
        var selectedIdentity = (_usbList.SelectedItem as UsbListItem)?.Snapshot.PhysicalIdentity?.UniqueId;
        var selectedRoot = (_usbList.SelectedItem as UsbListItem)?.Snapshot.RootPath;
        try
        {
            var prefetch = _usbPrefetch;
            _usbPrefetch = null;
            var discovery = prefetch is { } early && DateTime.UtcNow - early.StartedUtc < TimeSpan.FromSeconds(15)
                ? early.Discovery
                : _dependencies.InspectUsbDrives(CancellationToken.None);
            _usbSnapshots = (await discovery).ToArray();
            _usbDiscoveryCompleted = true;
        }
        finally
        {
            _usbRefreshInProgress = false;
        }

        if (_step != LauncherWizardStep.Usb || _usbList is null)
        {
            return;
        }

        // Measurements are registered first, so the card shows "measuring" at once.
        StartUsbSpeedMeasurements();
        PopulateUsbList(selectedIdentity, selectedRoot);
    }

    private void PopulateUsbList(string? selectedIdentity, string? selectedRoot)
    {
        if (_usbList is null) return;
        var items = _usbSnapshots.Select(snapshot => (object)new UsbListItem(snapshot)).ToList();
        var restored = items.Cast<UsbListItem>()
            .Select((item, index) => (item, index))
            .FirstOrDefault(pair =>
                (!string.IsNullOrWhiteSpace(selectedIdentity)
                    && string.Equals(pair.item.Snapshot.PhysicalIdentity?.UniqueId, selectedIdentity, StringComparison.OrdinalIgnoreCase))
                || string.Equals(pair.item.Snapshot.RootPath, selectedRoot, StringComparison.OrdinalIgnoreCase));
        _usbList.SetItems(items, restored.item is null ? 0 : restored.index);

        UsbSearching = false;
        var found = items.Count > 0;
        _usbList.Visible = found;
        if (_usbInfo is not null) _usbInfo.Visible = found;
        if (_usbListHeader?.Controls["WizardUsbRefresh"] is { } refresh) refresh.Visible = found;
        if (_usbSearch is not null)
        {
            _usbSearch.Visible = !found;
            if (!found && _usbSearch.Controls["WizardUsbEmpty"] is EmptyState empty)
            {
                // Nothing connected: what to do, and the refresh button in the card itself.
                empty.Set(_localizer.T("launcher.wizard.usb.empty_title"), _localizer.T("launcher.wizard.usb.empty_detail"),
                    _localizer.T("launcher.wizard.usb.refresh"), () => RunGuardedAsync(RefreshUsbCandidatesAsync));
            }
        }

        UpdateSelectedUsbDetails();
    }

    /// <summary>
    /// Measures each writable drive once, off the discovery path, so the list appears as
    /// soon as Windows has answered and the speed fills in when it is known.
    /// </summary>
    private void StartUsbSpeedMeasurements()
    {
        if (_dependencies.MeasureUsbSpeed is not { } measure) return;
        foreach (var snapshot in _usbSnapshots)
        {
            var key = UsbSpeedKey(snapshot);
            if (snapshot.AccessSpeed is not null
                || !snapshot.IsReady
                || !snapshot.IsWritable
                || string.IsNullOrWhiteSpace(snapshot.RootPath)
                || key is null
                || _usbSpeeds.ContainsKey(key))
            {
                continue;
            }

            _usbSpeeds[key] = null;
            _ = MeasureUsbSpeedAsync(measure, snapshot, key);
        }
    }

    private async Task MeasureUsbSpeedAsync(
        Func<UsbDriveSnapshot, CancellationToken, Task<UsbAccessSpeedResult?>> measure,
        UsbDriveSnapshot snapshot,
        string key)
    {
        UsbAccessSpeedResult? result = null;
        try
        {
            result = await measure(snapshot, CancellationToken.None);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // An unmeasured drive is still a usable drive; the card just omits the speed.
        }

        if (IsDisposed) return;
        _usbSpeeds[key] = result ?? UsbAccessSpeedUnavailable;
        if (_step == LauncherWizardStep.Usb
            && _usbList?.SelectedItem is UsbListItem selected
            && UsbSpeedKey(selected.Snapshot) == key)
        {
            UpdateSelectedUsbDetails();
        }
    }

    private static readonly UsbAccessSpeedResult UsbAccessSpeedUnavailable = new(-1, -1);

    /// <summary>"F: Kingston DataTraveler 3.0 · 57.8 GB", the stick's icon or a drive's, and the external-disk badge.</summary>
    private (string Title, WizardIcon Icon, string? Badge) DescribeUsbField(UsbDriveSnapshot snapshot)
    {
        var drive = !string.IsNullOrWhiteSpace(snapshot.RootPath)
            ? snapshot.RootPath.TrimEnd('\\')
            : snapshot.PhysicalIdentity is { } identity ? $"Disk {identity.DiskNumber}" : "USB";
        var name = snapshot.PhysicalIdentity?.FriendlyName;
        if (string.IsNullOrWhiteSpace(name)) name = snapshot.VolumeLabel;
        var title = string.IsNullOrWhiteSpace(name) ? drive : $"{drive} {name}";
        if (snapshot.TotalSizeBytes is { } size) title += " · " + FormatBytes(size);
        return (title,
            snapshot.IsExternalDisk ? WizardIcon.ExternalDrive : WizardIcon.Usb,
            snapshot.IsExternalDisk ? _localizer.T("launcher.wizard.usb.external_badge") : null);
    }

    private static string? UsbSpeedKey(UsbDriveSnapshot snapshot)
    {
        var key = snapshot.PhysicalIdentity?.UniqueId;
        return string.IsNullOrWhiteSpace(key) ? snapshot.RootPath : key;
    }

    private void UpdateSelectedUsbDetails()
    {
        if (_usbStatus is null || _usbDetails is null || _usbStatusMark is null || _usbEraseNotice is null)
        {
            return;
        }

        if (!_usbDiscoveryCompleted)
        {
            return;
        }

        if (_usbList?.SelectedItem is not UsbListItem item)
        {
            _usbStatus.Text = _localizer.T("launcher.wizard.usb.none");
            _usbStatusMark.Kind = RowStatus.Kind.Warning;
            _usbDetails.Text = _localizer.T("launcher.wizard.usb.none_details");
            ShowUsbFacts(snapshot: null);
            ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), false);
            return;
        }

        var snapshot = item.Snapshot;
        _usbEraseNotice.Text = _localizer.T(snapshot.IsExternalDisk ? "launcher.wizard.usb.external_notice" : "launcher.wizard.usb.erase_description");
        if (snapshot.PhysicalIdentity?.IsReadOnly == true)
        {
            _usbStatus.Text = _localizer.T("launcher.wizard.usb.status_read_only");
            _usbStatusMark.Kind = RowStatus.Kind.Error;
            _usbDetails.Text = _localizer.T("launcher.wizard.usb.read_only_details");
            ShowUsbFacts(snapshot: null);
            ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), false);
            return;
        }

        if (snapshot.NeedsFormatting)
        {
            _usbStatus.Text = _localizer.T(snapshot.IsExternalDisk ? "launcher.wizard.usb.status_check" : "launcher.wizard.usb.needs_formatting");
            _usbStatusMark.Kind = snapshot.IsExternalDisk ? RowStatus.Kind.Warning : RowStatus.Kind.Ok;
            _usbDetails.Text = string.Empty;
            ShowUsbFacts(snapshot);
            ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), snapshot.CanProvision);
            return;
        }

        var status = !snapshot.IsWritable ? _localizer.T("launcher.wizard.usb.status_read_only")
            : snapshot.IsExternalDisk ? _localizer.T("launcher.wizard.usb.status_check")
            : _localizer.T("launcher.wizard.usb.status_ready");
        _usbStatus.Text = status;
        _usbStatusMark.Kind = !snapshot.IsWritable ? RowStatus.Kind.Error : snapshot.IsExternalDisk ? RowStatus.Kind.Warning : RowStatus.Kind.Ok;
        ShowUsbFacts(snapshot);
        var details = snapshot.IsWritable ? string.Empty : _localizer.T("launcher.wizard.usb.read_only_details");
        var key = UsbSpeedKey(snapshot);
        var speed = snapshot.AccessSpeed
            ?? (key is not null && _usbSpeeds.TryGetValue(key, out var measured) ? measured : null);
        if (speed is { WriteMegabytesPerSecond: >= 0 } && speed.IsSlow)
        {
            // Only a slow stick earns a line of its own; a normal one says nothing more.
            details += (details.Length > 0 ? Environment.NewLine : string.Empty)
                + _localizer.T("launcher.wizard.usb.speed", Args(
                    ("write", FormatNumber(speed.WriteMegabytesPerSecond, "0.0")),
                    ("read", FormatNumber(speed.ReadMegabytesPerSecond, "0.0"))))
                // The figures end without a full stop; the verdict is a sentence of its own.
                + ". " + _localizer.T("launcher.wizard.usb.speed_slow");
            _usbStatus.Text = _localizer.T("launcher.wizard.usb.status_slow");
            _usbStatusMark.Kind = RowStatus.Kind.Warning;
        }

        _usbDetails.Text = details;
        ConfigureActions(true, _localizer.T("launcher.primary_action_continue"), snapshot.CanProvision);
    }

    /// <summary>The drive's facts in the check card, or none when there is no drive to describe.</summary>
    private void ShowUsbFacts(UsbDriveSnapshot? snapshot)
    {
        if (_usbFacts is null || _usbEraseNotice is null) return;
        _usbFacts.Visible = snapshot is not null;
        _usbEraseNotice.Visible = snapshot?.IsWritable == true && snapshot.PhysicalIdentity?.IsReadOnly != true;
        if (snapshot is null) return;
        _usbFacts.SetFacts(
        [
            (_localizer.T("launcher.wizard.usb.fact_drive"), DisplayDrive(snapshot)),
            (_localizer.T("launcher.wizard.usb.fact_model"), snapshot.PhysicalIdentity?.FriendlyName ?? "—"),
            (_localizer.T("launcher.wizard.usb.fact_capacity"), snapshot.TotalSizeBytes is { } total ? FormatBytes(total) : "—"),
            (_localizer.T("launcher.wizard.usb.fact_free"), snapshot.AvailableFreeSpaceBytes is { } free ? FormatBytes(free) : "—")
        ]);
    }

    // ------------------------------------------------------------------ step 5

    private async Task StartCreateAsync()
    {
        var selected = (_usbList?.SelectedItem as UsbListItem)?.Snapshot;
        _selectedUsb = !string.IsNullOrWhiteSpace(selected?.RootPath)
            ? selected.RootPath
            : selected?.PhysicalIdentity is { } identity
                ? $"PhysicalDrive{identity.DiskNumber}"
                : null;
        if (string.IsNullOrWhiteSpace(_selectedUsb) || _preparedImage is null || selected?.CanProvision != true)
        {
            await ShowUsbAsync();
            return;
        }

        var hasEnoughCapacity = selected.TotalSizeBytes is { } capacity
            ? UsbProvisioningCapacity.TryCalculateDataImageSize(capacity, _preparedImage.SourceSizeBytes, out _)
            : _dependencies.HasEnoughSpace(_selectedUsb, _preparedImage.SourceSizeBytes);
        if (!hasEnoughCapacity)
        {
            if (_usbStatus is not null && _usbStatusMark is not null && _usbDetails is not null)
            {
                _usbStatus.Text = _localizer.T("launcher.wizard.usb.status_read_only_space");
                _usbStatusMark.Kind = RowStatus.Kind.Error;
                _usbDetails.Text = _localizer.T("launcher.wizard.usb.no_space");
            }

            return;
        }

        if (!_dependencies.ConfirmErase(selected))
        {
            return;
        }

        _lastCreateImage = _preparedImage.LocalImagePath;
        BuildCreatePage(failed: false);
        // Only "Stop writing" while the stick is written; the page itself shows the progress.
        ConfigureActions(false, _localizer.T("launcher.wizard.create.working"), false, PrimaryMark.None,
            secondaryText: null, showPrimary: false);
        _closeButton.Text = _localizer.T("launcher.wizard.create.abort");
        _closeButton.FitWidth();
        LayoutActionBar();

        Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, _preparedImage.LocalImagePath);
        Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, _preparedImage.SourceSha256);
        Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, DexTorStorage.Release);

        _creatingUsb = true;
        _abortRequested = false;
        SetBusy(true);
        CreatorActionService.CreateUsbResult result;
        var hostGuard = _dependencies.PrepareHostForCreation?.Invoke(selected);
        try
        {
            _createTracker = null;
            _lastCreateSpeed = null;
            _lastCreateProgress = null;
            var progress = new Progress<CreatorActionService.CreatorStepProgress>(UpdateCreateProgress);
            // Phases that report no bytes - clearing the stick, creating the data volume -
            // took up to half a minute with nothing moving; the last report is replayed
            // every second so the bar creeps and the elapsed time counts.
            using var heartbeat = new System.Windows.Forms.Timer { Interval = 1000 };
            heartbeat.Tick += (_, _) =>
            {
                if (_lastCreateProgress is { } last) UpdateCreateProgress(last);
            };
            heartbeat.Start();
            result = await _dependencies.CreateUsb(selected, progress);
            heartbeat.Stop();
        }
        catch (Exception exception)
        {
            result = new CreatorActionService.CreateUsbResult(
                false,
                _selectedUsb,
                CreatorActionService.CreateTransaction(),
                exception.Message);
        }
        finally
        {
            _creatingUsb = false;
            _closeButton.Text = _localizer.T("launcher.close");
            _closeButton.FitWidth();
            SetBusy(false);
            // The drive letter is given just before the provisioner exits, and Windows
            // raises AutoPlay for it a little later; the guard outlives that moment.
            _ = ReleaseLaterAsync(hostGuard, TimeSpan.FromSeconds(8));
        }

        if (result.Success)
        {
            ShowComplete(result);
        }
        else
        {
            ShowCreateFailed(result);
        }
    }

    private void BuildCreatePage(bool failed)
    {
        SetStep(LauncherWizardStep.Create, failed);
        var page = CreatePage(
            _localizer.T("launcher.wizard.create.title"),
            _localizer.T(failed ? "launcher.wizard.create.stopped" : "launcher.wizard.create.description"));
        _activatedBeforeDriveLetter = false;
        _transferStage = null;
        _transferSamples.Clear();
        _smoothedSpeed = null;
        var card = CreateInfoPanel();
        card.Name = "WizardStatusCard";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.create.caption")));
        _createStages = new StageChecklist { Name = "WizardCreateStages", Width = S(RowWidth), Margin = S(0, 0, 0, 0) };
        _createStages.SetStages(CreateStageKeys.Select(key => _localizer.T(key)).ToArray());
        _createStages.Advance(0);
        card.Controls.Add(_createStages);
        // The figures of the current phase; the operator sees the stages and the bar.
        _pageStatus = new WizardText(14, 600, 20, WizardDesign.Text) { Name = "WizardStatusTitle", Visible = false };
        _pageDetail = new WizardText(14, 400, 20, WizardDesign.TextSecondary) { Name = "WizardStatusDetail", Visible = false };
        card.Controls.Add(_pageStatus);
        card.Controls.Add(_pageDetail);
        _transferStats = new TransferStatsView(56) { Name = "WizardTransferStats", Width = S(RowWidth), Visible = false, Margin = S(0, 4, 0, 0) };
        card.Controls.Add(_transferStats);
        _pageProgress = new WizardProgressBar { Name = "WizardProgressBar", Width = S(RowWidth), Margin = S(0, 12, 0, 0), Maximum = 1000 };
        card.Controls.Add(_pageProgress);
        _statusCard = card;
        AddToPage(page, card);
    }

    private static async Task ReleaseLaterAsync(IDisposable? guard, TimeSpan after)
    {
        if (guard is null) return;
        await Task.Delay(after);
        guard.Dispose();
    }

    private void UpdateCreateProgress(CreatorActionService.CreatorStepProgress progress)
    {
        if (_pageStatus is null || _pageProgress is null)
        {
            return;
        }

        if (progress.Step is CreatorStep.VerifyUsb or CreatorStep.Complete
            && !_activatedBeforeDriveLetter && IsHandleCreated && Visible && Form.ActiveForm != this)
        {
            // The drive gets its letter right after verification. Windows asks only the
            // foreground window whether to cancel AutoPlay, so the wizard is brought back
            // in front first; otherwise Explorer opens a window on the new drive.
            _activatedBeforeDriveLetter = true;
            Activate();
        }

        // One bar and one percentage for the whole run; see CreateProgressTracker.
        _lastCreateProgress = progress;
        var snapshot = (_createTracker ??= new CreateProgressTracker(() => _transferClock.Elapsed)).Observe(progress);
        _pageProgress.Maximum = 1000;
        _pageProgress.Value = Math.Max(_pageProgress.Value, (int)Math.Round(snapshot.Overall * 1000));
        var percent = (int)Math.Floor(snapshot.Overall * 100);
        _createStages?.Advance(CreateStageIndex(snapshot.Phase), snapshot.Phase == CreateProgressTracker.Phase.Complete);
        _pageStatus.Text = _localizer.T("launcher.create.progress.current", Args(("step", _localizer.T(PhaseNameKey(snapshot.Phase)))));
        if (!string.IsNullOrWhiteSpace(progress.MessageKey) && _pageDetail is not null)
        {
            var detail = _localizer.T(progress.MessageKey, progress.MessageArgs);
            if (snapshot.PhaseBytesTotal <= 0 && snapshot.Phase != CreateProgressTracker.Phase.Complete && snapshot.PhaseElapsed >= TimeSpan.FromSeconds(3))
            {
                // A phase without bytes still shows that it is alive.
                detail += " " + FormatDuration(snapshot.PhaseElapsed);
            }

            if (!string.Equals(_pageDetail.Text, detail, StringComparison.Ordinal))
            {
                _pageDetail.Text = detail;
            }
        }

        // The stage's own counter: both writes on one, both reads on the other. It stays on
        // through the short phases without bytes in between, with the time left.
        if (snapshot.StageBytesTotal > 0)
        {
            if (snapshot.Speed != _shownSpeedKind && snapshot.Speed != CreateProgressTracker.SpeedKind.None)
            {
                _shownSpeedKind = snapshot.Speed;
                _transferStats?.ResetHistory();
            }

            // A pause of a few seconds - formatting, a new pass starting - keeps the last speed
            // rather than blanking it while the counter goes on.
            if (snapshot.BytesPerSecond is { } s) _lastCreateSpeed = FormatBytesPerSecond(s);
            var speedText = _lastCreateSpeed ?? "—";
            var etaText = snapshot.Remaining is { } left ? FormatDuration(left) : "—";
            ShowTransferStats(
                _localizer.T("launcher.wizard.create.bytes_progress", Args(
                    ("percent", percent.ToString(CultureInfo.InvariantCulture)),
                    ("done", FormatBytes(snapshot.StageBytesDone)),
                    ("total", FormatBytes(snapshot.StageBytesTotal)),
                    ("speed", speedText),
                    ("eta", etaText))),
                [
                    (_localizer.T("launcher.wizard.create.stat_speed"), speedText),
                    (_localizer.T("launcher.wizard.create.stat_processed"), $"{FormatBytes(snapshot.StageBytesDone)} / {FormatBytes(snapshot.StageBytesTotal)}"),
                    (_localizer.T("launcher.wizard.image.stat_remaining"), etaText)
                ],
                snapshot.BytesPerSecond,
                percent);
        }
        else
        {
            // Stages without a size are short; the chart of the last transfer would only
            // mislead, so it steps aside until the next one.
            HideTransferStats();
        }
    }

    private bool _creatingUsb;
    private bool _launching;
    private bool _abortRequested;

    /// <summary>
    /// "Stop writing?" - with keeping on as the safe answer. Confirmed, the provisioner
    /// is asked to stop and the page says so until it has.
    /// </summary>
    private void AskToAbortCreation()
    {
        if (_abortRequested || _dependencies.CancelCreation is null) return;
        if (!AskConfirmation(this, _localizer, _localizer.T("launcher.wizard.create.abort_confirm"),
                _localizer.T("launcher.wizard.create.abort_short"), _localizer.T("launcher.wizard.create.abort_keep"), destructive: true,
                ConfirmationStyle.Abort))
        {
            return;
        }

        if (!_creatingUsb || !_dependencies.CancelCreation()) return;
        _abortRequested = true;
        _closeButton.Enabled = false;
        if (_pageStatus is not null) _pageStatus.Text = _localizer.T("launcher.wizard.create.aborting");
        if (_pageDetail is not null) _pageDetail.Text = _localizer.T("launcher.wizard.create.aborting");
    }

    private StageChecklist? _createStages;

    /// <summary>The four stages the design shows; the tracker's finer phases fold into them.</summary>
    private static readonly string[] CreateStageKeys =
    [
        "launcher.wizard.create.stage_erase",
        "launcher.wizard.create.stage_write",
        "launcher.wizard.create.stage_verify",
        "launcher.wizard.create.stage_done"
    ];

    /// <summary>
    /// Which of the four lines is on: everything that writes is "Writing the image", and
    /// "Checking the write" is the read-back of both from the stick, at the end.
    /// </summary>
    internal static int CreateStageIndex(CreateProgressTracker.Phase phase) => phase switch
    {
        CreateProgressTracker.Phase.Preparing or CreateProgressTracker.Phase.CheckingImage or CreateProgressTracker.Phase.ErasingUsb => 0,
        CreateProgressTracker.Phase.WritingBootImage
            or CreateProgressTracker.Phase.CreatingDataVolume
            or CreateProgressTracker.Phase.CreatingStorage
            or CreateProgressTracker.Phase.CopyingWindowsImage => 1,
        CreateProgressTracker.Phase.FinalCheck => 2,
        _ => 3
    };

    private CreateProgressTracker? _createTracker;
    private string? _lastCreateSpeed;
    private CreatorActionService.CreatorStepProgress? _lastCreateProgress;
    private CreateProgressTracker.SpeedKind _shownSpeedKind;

    private static string PhaseNameKey(CreateProgressTracker.Phase phase) =>
        "launcher.wizard.create.phase_" + phase.ToString().ToLowerInvariant();

    /// <summary>
    /// A write that failed stays on the creation step: the stage that broke turns red, the
    /// block says what happened and what to do, and "Start again" goes back to the drives.
    /// </summary>
    private void ShowCreateFailed(CreatorActionService.CreateUsbResult result)
    {
        _lastCreateResult = result;
        _creationSucceeded = false;
        var reached = _createStages?.Current ?? 0;
        BuildCreatePage(failed: true);
        _createStages!.Advance(Math.Max(0, reached));
        _createStages.Fail();
        _pageProgress!.Visible = false;
        var message = result.ErrorMessageKey is null
            ? result.ErrorMessage ?? _localizer.T("launcher.action.create.error.unknown")
            : _localizer.T(result.ErrorMessageKey, result.ErrorMessageArgs);
        var solution = _localizer.T(result.ErrorMessageKey == "launcher.create.error.image"
            ? "launcher.wizard.complete.retry_image"
            : result.ErrorMessageKey?.StartsWith("launcher.create.error.device.", StringComparison.Ordinal) == true
                ? CreatorActionService.GetCreateErrorSolutionKey(result.ErrorMessageKey)
                : "launcher.wizard.complete.retry");
        var block = new ErrorBlock(S(RowWidth), _localizer.T("launcher.wizard.create.failed_title"), message + " " + solution)
        {
            Name = "WizardCreateError", Margin = S(0, 12, 0, 0)
        };
        _statusCard!.Controls.Add(block);
        _pageStatus = block.TitleText;
        _pageDetail = block.DetailText;
        var details = new List<(string, string)>();
        if (!string.IsNullOrWhiteSpace(result.ErrorSafetyMessageKey))
        {
            details.Add((_localizer.T("launcher.create.error.safety.label").TrimEnd(':'), _localizer.T(result.ErrorSafetyMessageKey)));
        }

        if (!string.IsNullOrWhiteSpace(result.JournalPath))
        {
            details.Add((_localizer.T("launcher.wizard.create.details_journal"), result.JournalPath));
        }

        if (details.Count > 0) AddDetails(_statusCard, details.ToArray(), offset: 72);
        ConfigureActions(false, _localizer.T("launcher.wizard.complete.start_over"), true, PrimaryMark.None);
    }

    // ------------------------------------------------------------------ step 6

    private void ShowComplete(CreatorActionService.CreateUsbResult result)
    {
        _lastCreateResult = result;
        _creationSucceeded = result.Success;
        if (!result.Success)
        {
            ShowCreateFailed(result);
            return;
        }

        SetStep(LauncherWizardStep.Complete);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.complete.title"),
            _localizer.T("launcher.wizard.complete.description"));
        var card = CreateInfoPanel();
        card.Name = "WizardStatusCard";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.complete.caption")));
        AddWhatNextRows(card, ImageName());
        AddToPage(panel, card);
        if (_usbOnly)
        {
            ConfigureActions(false, _localizer.T("launcher.wizard.complete.done"), true, PrimaryMark.None, showSecondary: false);
        }
        else
        {
            ConfigureActions(false, _localizer.T("launcher.primary_action_start_dextor"), true, PrimaryMark.Play);
        }
    }

    private async Task ShowExistingUsbReadyAsync()
    {
        SetStep(LauncherWizardStep.Complete);
        var panel = CreatePage(
            _localizer.T("launcher.wizard.existing.title"),
            _localizer.T("launcher.wizard.existing.description"));
        var card = CreateInfoPanel();
        card.Name = "WizardStatusCard";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.complete.caption")));
        AddWhatNextRows(card, null);
        AddToPage(panel, card);
        ConfigureActions(false, _localizer.T("launcher.primary_action_start_dextor"), true, PrimaryMark.Play);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The design's "what next": the stick that was made, the ways to start DexTor OS, and
    /// what to keep in mind - one less way, and one more note, when it runs only from the stick.
    /// </summary>
    private void AddWhatNextRows(WizardInfoPanel card, string? imageName)
    {
        var stick = CreateRow("WizardNextUsb", WizardIcon.Usb,
            imageName is null
                ? _localizer.T("launcher.wizard.existing.drive", Args(("drive", _environment?.Context.DextorRootPath ?? string.Empty)))
                : _localizer.T("launcher.wizard.complete.usb_name", Args(("name", imageName))),
            _localizer.T("launcher.wizard.complete.next"));
        stick.Tile!.DoneBadge = true;
        card.Controls.Add(stick);
        _statusRow = stick;
        _pageStatus = stick.TitleText;
        _pageDetail = stick.DetailText;
        if (!_usbOnly)
        {
            card.Controls.Add(CreateRow("WizardNextWindows", WizardIcon.Monitor, _localizer.T("launcher.wizard.complete.from_windows"),
                _localizer.T("launcher.wizard.complete.from_windows_detail")));
        }

        card.Controls.Add(CreateRow("WizardNextBoot", WizardIcon.Restart, _localizer.T("launcher.wizard.complete.from_boot"),
            _localizer.T("launcher.wizard.complete.from_boot_detail")));
        if (_usbOnly)
        {
            card.Controls.Add(CreateNotice("WizardNoVirtualizationNotice", Tone.Warning, WizardIcon.Info, _localizer.T("launcher.wizard.complete.no_virtualization")));
        }

        var eject = CreateNotice("WizardEjectNotice", Tone.Warning, WizardIcon.Info, _localizer.T("launcher.wizard.complete.eject_notice"));
        if (_usbOnly) eject.Margin = S(0, 10, 0, 0);
        card.Controls.Add(eject);
    }

    /// <summary>
    /// The launch card that replaces "what next" while DexTor OS starts, runs and stops: one
    /// row with the play icon, its status and detail, and the bar.
    /// </summary>
    private void BuildLaunchPage()
    {
        SetStep(LauncherWizardStep.Complete);
        var panel = CreatePage(
            _localizer.T(_existingUsbVerified ? "launcher.wizard.existing.title" : "launcher.wizard.complete.title"),
            _localizer.T(_existingUsbVerified ? "launcher.wizard.existing.description" : "launcher.wizard.complete.description"));
        var card = CreateStatusCard(_localizer.T("launcher.wizard.vm.launch_caption"), WizardIcon.Play,
            _localizer.T("launcher.wizard.vm.starting"), _localizer.T("launcher.wizard.vm.starting_patience"),
            transfer: false, progress: true);
        // Two lines, whatever the step says: a one-line detail moved the steps and the bar up.
        _pageDetail!.MinimumSize = new Size(0, S(20) * 2);
        _statusRow?.Relayout();
        // The steps of the boot, under the row: DexTor OS boots with its window hidden and
        // the operator follows it here (see VmBootProgress).
        _launchStages = new StageChecklist { Name = "WizardLaunchStages", Width = S(RowWidth), Margin = S(0, 12, 0, 0) };
        _launchStages.SetStages(VmBootProgress.StageKeys.Select(key => _localizer.T(key)).ToArray());
        _launchStages.Advance(0);
        _launchElapsed = new WizardText(13, 400, 18, WizardDesign.TextSecondary) { Name = "WizardLaunchElapsed", Width = S(RowWidth), Margin = S(0, 8, 0, 0) };
        var bar = card.Controls.Find("WizardProgressBar", false).SingleOrDefault();
        var at = bar is null ? card.Controls.Count : card.Controls.GetChildIndex(bar);
        card.Controls.Add(_launchStages);
        card.Controls.SetChildIndex(_launchStages, at);
        card.Controls.Add(_launchElapsed);
        card.Controls.SetChildIndex(_launchElapsed, at + 1);
        AddToPage(panel, card);
    }

    private StageChecklist? _launchStages;
    private WizardText? _launchElapsed;
    private VmBootProgress? _bootProgress;
    private bool _bootDownloading;
    private readonly System.Diagnostics.Stopwatch _bootClock = new();
    private System.Windows.Forms.Timer? _bootTimer;

    private GlassNotice? _slowNotice;

    /// <summary>
    /// Three minutes without a picture: said plainly, with a way to wait on and a way to look -
    /// the VM window itself, whatever is on it. The window is never shown on its own for this.
    /// </summary>
    private void ShowSlowStart()
    {
        if (_statusCard is null || _slowNotice is { IsDisposed: false }) return;
        _slowNotice = CreateNotice("WizardLaunchSlow", Tone.Warning, WizardIcon.Warning, _localizer.T("launcher.wizard.vm.slow"));
        _slowNotice.Margin = S(0, 12, 0, 0);
        if (_dependencies.ShowVmWindowNow is { } showNow)
        {
            _slowNotice.Action = _localizer.T("launcher.wizard.vm.slow_show");
            _slowNotice.ActionClicked += (_, _) =>
            {
                showNow();
                HideSlowStart();
            };
        }

        _statusCard.Controls.Add(_slowNotice);
        ConfigureActions(false, _localizer.T("launcher.wizard.vm.launching_button"), false, PrimaryMark.Spinner,
            secondaryText: _localizer.T("launcher.wizard.vm.slow_wait"),
            secondaryAction: () =>
            {
                HideSlowStart();
                return Task.CompletedTask;
            });
    }

    private void HideSlowStart()
    {
        if (_slowNotice is not { IsDisposed: false } notice) return;
        _slowNotice = null;
        notice.Parent?.Controls.Remove(notice);
        notice.Dispose();
        if (_launching) ConfigureActions(false, _localizer.T("launcher.wizard.vm.launching_button"), false, PrimaryMark.Spinner);
    }

    /// <summary>The bar and the elapsed line, between the guest's own steps too.</summary>
    private void ShowBootProgress()
    {
        if (_bootProgress is not { } boot || _pageProgress is null) return;
        _pageProgress.IsIndeterminate = false;
        _pageProgress.Maximum = 1000;
        _pageProgress.Value = (int)Math.Round(boot.Fraction(_bootClock.Elapsed) * 1000);
        _launchStages?.Advance(boot.Stage);
        if (_launchElapsed is not null)
        {
            var elapsed = FormatDuration(_bootClock.Elapsed);
            _launchElapsed.Text = boot.LastBoot is { } typical
                ? _localizer.T("launcher.wizard.vm.boot_elapsed", Args(("elapsed", elapsed), ("typical", FormatDuration(typical))))
                : _localizer.T("launcher.wizard.vm.boot_elapsed_first", Args(("elapsed", elapsed)));
        }
    }

    private void StopBootProgress(bool booted)
    {
        HideSlowStart();
        _bootTimer?.Stop();
        _bootTimer?.Dispose();
        _bootTimer = null;
        if (booted && _bootClock.IsRunning) VmBootProgress.RememberBoot(_bootClock.Elapsed);
        _bootClock.Reset();
        if (booted)
        {
            _launchStages?.Advance(VmBootProgress.StageKeys.Length - 1, finished: true);
            if (_launchElapsed is not null) _launchElapsed.Visible = false;
        }
    }

    private void ShowLaunchFailed(string reason)
    {
        SetStep(LauncherWizardStep.Complete);
        var panel = CreatePage(
            _localizer.T(_existingUsbVerified ? "launcher.wizard.existing.title" : "launcher.wizard.complete.title"),
            _localizer.T(_existingUsbVerified ? "launcher.wizard.existing.description" : "launcher.wizard.complete.description"));
        var card = CreateInfoPanel();
        card.Name = "WizardStatusCard";
        card.Controls.Add(CreateCaption(_localizer.T("launcher.wizard.vm.launch_caption")));
        var block = new ErrorBlock(S(RowWidth), _localizer.T("launcher.wizard.vm.failed"), _localizer.T("launcher.wizard.vm.failed_detail")) { Name = "WizardLaunchError" };
        card.Controls.Add(block);
        _pageStatus = block.TitleText;
        _pageDetail = block.DetailText;
        _pageProgress = null;
        if (!string.IsNullOrWhiteSpace(reason))
        {
            AddDetails(card, [(_localizer.T("launcher.wizard.image.details_error"), reason)], offset: 72);
        }

        AddToPage(panel, card);
        ConfigureActions(false, _localizer.T("launcher.wizard.vm.retry"), true, PrimaryMark.None);
    }

    private async Task HandlePrimaryAsync()
    {
        if (_busy)
        {
            return;
        }

        switch (_step)
        {
            case LauncherWizardStep.Intro:
                await ShowHostCheckAsync();
                break;
            case LauncherWizardStep.HostCheck:
                await HandleHostPrimaryAsync();
                break;
            case LauncherWizardStep.Image:
                if (_preparedImage?.Success == true)
                {
                    await ShowUsbAsync();
                }
                else if (_imageInspection?.Success == true || _imageFailure is not null)
                {
                    await DownloadImageAsync();
                }
                else
                {
                    await ShowImageAsync();
                }
                break;
            case LauncherWizardStep.Usb:
                if (_usbList?.SelectedItem is UsbListItem { Snapshot.CanProvision: true })
                {
                    await StartCreateAsync();
                }
                else
                {
                    await RefreshUsbCandidatesAsync();
                }
                break;
            case LauncherWizardStep.Create:
                // "Start again" after a failed write: back to choosing the drive; the
                // downloaded image is kept.
                _lastCreateResult = null;
                _selectedUsb = null;
                await ShowUsbAsync();
                break;
            case LauncherWizardStep.Complete:
                if (_vmRunning)
                {
                    if (_vmPausedForSpace) await ResumeVmAsync();
                    else await StopVmAsync();
                }
                else if (_ejectAvailable)
                {
                    await EjectUsbAsync();
                }
                else if (_vmHasRun || _usbOnly)
                {
                    Close();
                }
                else if (_lastCreateResult?.Success == true && !string.IsNullOrWhiteSpace(_lastCreateResult.UsbRootPath))
                {
                    await StartVmCheckedAsync(_lastCreateResult.UsbRootPath);
                }
                else if (_environment?.Context.IsDexTorUsb == true && !string.IsNullOrWhiteSpace(_environment.Context.DextorRootPath))
                {
                    await StartVmCheckedAsync(_environment.Context.DextorRootPath);
                }
                else if (!_creationSucceeded)
                {
                    _environment = null;
                    _imageInspection = null;
                    _preparedImage = null;
                    _selectedUsb = null;
                    _lastCreateResult = null;
                    ShowIntro();
                }
                else
                {
                    Close();
                }
                break;
        }
    }

    private async Task StartVmAsync(string usbRoot)
    {
        BuildLaunchPage();
        if (_pageStatus is null || _pageDetail is null || _pageProgress is null) return;
        _pageProgress.Visible = true;
        // Empty and still from the first moment, not a sweep that later turns into a bar.
        _pageProgress.IsIndeterminate = false;
        _pageProgress.Maximum = 1000;
        _pageProgress.Value = 0;
        _bootProgress = new VmBootProgress(VmBootProgress.ReadLastBoot());
        _bootDownloading = false;
        _bootClock.Restart();
        _bootTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _bootTimer.Tick += (_, _) => { if (!_bootDownloading) ShowBootProgress(); };
        _bootTimer.Start();
        ShowBootProgress();
        ConfigureActions(false, _localizer.T("launcher.wizard.vm.launching_button"), false, PrimaryMark.Spinner);
        _launching = true;
        SetBusy(true);
        Update();
        await Task.Yield();
        var failed = false;
        string? reason = null;
        try
        {
            var progress = new Progress<SessionStartProgress>(update =>
            {
                if (_pageStatus is null || _pageDetail is null || _pageProgress is null) return;
                if (update.Stage == SessionStartStage.Running)
                {
                    StopBootProgress(booted: true);
                    _pageProgress.IsIndeterminate = false;
                    _pageProgress.Value = _pageProgress.Maximum;
                    _pageStatus.Text = _localizer.T("launcher.wizard.vm.running");
                    _pageDetail.Text = _localizer.T("launcher.wizard.vm.running_details");
                    return;
                }

                // The design's launch card keeps its one title and patient detail; the
                // stage is named only where there are figures to show.
                if (update.TotalBytes is { } total && total > 0)
                {
                    _bootDownloading = true;
                    var percent = (int)Math.Clamp(update.BytesDownloaded * 100d / total, 0, 100);
                    _pageProgress.IsIndeterminate = false;
                    _pageProgress.Maximum = 100;
                    _pageProgress.Value = percent;
                    _pageStatus.Text = _localizer.T(SessionStageKey(update.Stage));
                    _pageDetail.Text = _localizer.T("launcher.wizard.image.progress", Args(
                        ("percent", percent.ToString(CultureInfo.InvariantCulture)),
                        ("downloaded", FormatBytes(update.BytesDownloaded)),
                        ("total", FormatBytes(total)),
                        ("speed", update.BytesPerSecond is { } speed ? FormatBytesPerSecond(speed) : "—"),
                        ("eta", update.EstimatedRemaining is { } eta ? FormatDuration(eta) : "—")));
                }
                else if (update.Stage == SessionStartStage.GuestSlow)
                {
                    ShowSlowStart();
                }
                else
                {
                    _bootDownloading = false;
                    _bootProgress?.Observe(update.Stage);
                    ShowBootProgress();
                    _pageStatus.Text = _localizer.T("launcher.wizard.vm.starting");
                    _pageDetail.Text = _localizer.T(update.Stage switch
                    {
                        SessionStartStage.GuestFirmware => "launcher.wizard.vm.boot_firmware",
                        SessionStartStage.GuestLoadingSystem => "launcher.wizard.vm.boot_system",
                        SessionStartStage.GuestShowing => "launcher.wizard.vm.boot_window",
                        _ => "launcher.wizard.vm.starting_patience"
                    });
                }
            });
            // Off the UI thread: preparing the runtime and the stick took seconds before the
            // first real wait, and the page - and the old button's glow - stood still meanwhile.
            var start = _dependencies.StartDexTor;
            await Task.Run(() => start(usbRoot, progress));
            StopBootProgress(booted: true);
            _vmRunning = true;
            _vmHasRun = true;
            _activeVmUsbRoot = usbRoot;
            _lastVmUsbRoot = usbRoot;
            _vmStateTimer.Start();
            _pageProgress.IsIndeterminate = false;
            _pageProgress.Value = _pageProgress.Maximum;
            _pageStatus.Text = _localizer.T("launcher.wizard.vm.running");
            _pageDetail.Text = _localizer.T("launcher.wizard.vm.running_details");
            MoveToTray();
        }
        catch (Exception exception)
        {
            failed = true;
            reason = exception.Message;
            StopBootProgress(booted: false);
        }
        finally
        {
            _launching = false;
            SetBusy(false);
            if (!failed)
            {
                ConfigureActions(false, _localizer.T(_vmRunning ? "launcher.wizard.vm.stop" : "launcher.primary_action_start_dextor"), true,
                    _vmRunning ? PrimaryMark.None : PrimaryMark.Play);
            }
        }

        if (failed) ShowLaunchFailed(reason ?? string.Empty);
    }

    private async Task StopVmAsync()
    {
        if (_pageStatus is null || _pageDetail is null || _pageProgress is null) return;

        // One destructive action at a time. A second Stop must not start a second
        // sequence, and the busy flag alone is not enough because the awaits below let
        // the pump keep running - which is the point.
        if (_vmStopRequested) return;
        _vmStopRequested = true;
        SetBusy(true);
        _pageProgress.Visible = true;
        _pageProgress.IsIndeterminate = true;
        _pageStatus.Text = _localizer.T("launcher.wizard.vm.stopping");

        // Progress<T> posts to the captured context, so these land on the UI thread.
        var progress = new Progress<SessionFinalizationStage>(ReportFinalizationStage);
        try
        {
            var report = await _dependencies.StopDexTor(progress);
            _vmRunning = false;
            _activeVmUsbRoot = null;
            _vmStateTimer.Stop();
            _pageProgress.Visible = false;
            ShowFinalizationOutcome(report);
            _ejectAvailable = report.WriterTermination == WriterTerminationResult.Confirmed
                && !report.CanRemoveUsb;
        }
        catch (Exception exception)
        {
            _pageProgress.Visible = false;
            _pageStatus.Text = _localizer.T("launcher.wizard.vm.stop_failed");
            _pageStatus.ForeColor = ErrorColor;
            _pageDetail.Text = exception.Message;
        }
        finally
        {
            _vmStopRequested = false;
            SetBusy(false);
            ConfigureActions(false, _localizer.T(NextCompleteActionKey()), true, PrimaryMark.None);
            RestoreFromTray();
        }
    }

    private NotifyIcon? _tray;
    private bool _trayHintShown;

    /// <summary>
    /// Steps aside while DexTor OS runs: the wizard window hides and the launcher waits in
    /// the notification area, from where it is opened again or DexTor OS is shut down.
    /// </summary>
    private void MoveToTray()
    {
        if (!IsHandleCreated || !Visible || _tray is not null) return;
        var menu = new ContextMenuStrip();
        menu.Items.Add(_localizer.T("launcher.tray.open"), null, (_, _) => RestoreFromTray());
        menu.Items.Add(_localizer.T("launcher.tray.stop"), null, (_, _) =>
        {
            // Stopping shows its progress and outcome in the window.
            RestoreFromTray();
            if (_vmRunning) _ = RunGuardedAsync(StopVmAsync);
        });
        var tooltip = _localizer.T("launcher.tray.tooltip");
        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = tooltip.Length > 63 ? tooltip[..63] : tooltip,
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();
        _tray.BalloonTipClicked += (_, _) => RestoreFromTray();
        Hide();
        // Coming back from the tray shows the window, not a taskbar button to click again.
        WindowState = FormWindowState.Normal;
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloonTip(5000, _localizer.T("launcher.tray.hidden_title"), _localizer.T("launcher.tray.hidden_text"), ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray()
    {
        var tray = _tray;
        if (tray is null) return;
        _tray = null;
        tray.Visible = false;
        tray.ContextMenuStrip?.Dispose();
        tray.Dispose();
        if (IsDisposed || _emergencyShutdownInProgress) return;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>
    /// Asks Windows to remove the device, as a separate action from stopping.
    /// </summary>
    private async Task EjectUsbAsync()
    {
        if (_pageStatus is null || _pageDetail is null || _pageProgress is null) return;
        if (_vmStopRequested || string.IsNullOrWhiteSpace(_lastVmUsbRoot)) return;

        _vmStopRequested = true;
        SetBusy(true);
        _pageProgress.Visible = true;
        _pageProgress.IsIndeterminate = true;
        _pageStatus.Text = _localizer.T("launcher.wizard.vm.requesting_removal");
        var progress = new Progress<SessionFinalizationStage>(ReportFinalizationStage);
        try
        {
            var report = await _dependencies.EjectDexTor(_lastVmUsbRoot, progress);
            _pageProgress.Visible = false;
            ShowFinalizationOutcome(report);

            // A veto stays a veto: the action remains available to retry, and nothing
            // else is attempted on the operator's behalf.
            _ejectAvailable = !report.CanRemoveUsb;
        }
        catch (Exception exception)
        {
            _pageProgress.Visible = false;
            _pageStatus.Text = _localizer.T("launcher.wizard.vm.stop_failed");
            _pageStatus.ForeColor = ErrorColor;
            _pageDetail.Text = exception.Message;
        }
        finally
        {
            _vmStopRequested = false;
            SetBusy(false);
            ConfigureActions(false, _localizer.T(NextCompleteActionKey()), true, PrimaryMark.None);
        }
    }

    private string NextCompleteActionKey() =>
        _vmRunning ? "launcher.wizard.vm.stop"
        : _ejectAvailable ? "launcher.wizard.vm.eject"
        : "launcher.wizard.complete.finish";

    private void ReportFinalizationStage(SessionFinalizationStage stage)
    {
        if (_pageStatus is null) return;
        _pageStatus.Text = _localizer.T(stage switch
        {
            SessionFinalizationStage.StoppingGuest => "launcher.wizard.vm.stopping_guest",
            SessionFinalizationStage.ForcingStop => "launcher.wizard.vm.forcing_stop",
            SessionFinalizationStage.FlushingStorage => "launcher.wizard.vm.flushing",
            SessionFinalizationStage.RequestingRemoval => "launcher.wizard.vm.requesting_removal",
            SessionFinalizationStage.TerminationUnconfirmed => "launcher.session.termination_unconfirmed",
            _ => "launcher.wizard.vm.stopping"
        });
    }

    /// <summary>
    /// Shows every outcome the report carries, not just the headline one.
    /// </summary>
    /// <remarks>
    /// A forced stop or unverified storage stays on screen even when Windows removed the
    /// device successfully. Making the main result green by hiding the rest is exactly
    /// what this is here to prevent.
    /// </remarks>
    private void ShowFinalizationOutcome(SessionFinalizationReport report)
    {
        if (_pageStatus is null || _pageDetail is null) return;

        var unconfirmed = report.WriterTermination != WriterTerminationResult.Confirmed;
        _pageStatus.Text = _localizer.T(unconfirmed
            ? "launcher.wizard.vm.stop_failed"
            : "launcher.wizard.vm.stopped");
        _pageStatus.ForeColor = unconfirmed || report.WasForced ? ErrorColor : SuccessColor;
        _pageDetail.Text = Program.DescribeFinalization(report, _localizer).Trim();
    }

    private void ObserveVmState()
    {
        if (!_vmRunning || _vmStopRequested || _emergencyShutdownInProgress || _dependencies.GetSessionState() == SessionState.Running) return;
        _vmStateTimer.Stop();
        _vmRunning = false;
        _vmHasRun = false;
        _vmPausedForSpace = false;
        if (_pageProgress is not null)
        {
            _pageProgress.IsIndeterminate = false;
            _pageProgress.Visible = false;
        }
        if (_pageStatus is not null)
        {
            _pageStatus.Text = _localizer.T("launcher.wizard.vm.exited");
            _pageStatus.ForeColor = ErrorColor;
        }
        if (_pageDetail is not null)
        {
            var error = _dependencies.GetSessionError();
            _pageDetail.Text = string.IsNullOrWhiteSpace(error)
                ? _localizer.T("launcher.wizard.vm.exited_details")
                : _localizer.T("launcher.wizard.vm.exited_with_error", Args(("error", error)));
        }
        SetBusy(false);
        ConfigureActions(false, _localizer.T("launcher.primary_action_start_dextor"), true, PrimaryMark.Play);
        // DexTor OS ended on its own: the launcher comes back with what happened.
        RestoreFromTray();
    }

    private bool _vmPausedForSpace;

    /// <summary>
    /// The stick filled up and QEMU paused DexTor OS (see SessionHostService.IsPausedForSpace):
    /// the launcher comes forward and asks for room, and "Continue" lets DexTor OS carry on.
    /// Nothing is lost meanwhile, and it is neither a hang nor a crash.
    /// </summary>
    private void ObserveVmPause()
    {
        if (!_vmRunning || _vmStopRequested || _dependencies.IsVmPausedForSpace is not { } pausedForSpace) return;
        var paused = pausedForSpace();
        if (paused == _vmPausedForSpace) return;
        _vmPausedForSpace = paused;
        if (paused)
        {
            RestoreFromTray();
            if (_pageStatus is not null)
            {
                _pageStatus.Text = _localizer.T("launcher.wizard.vm.paused_no_space");
                _pageStatus.ForeColor = ErrorColor;
            }

            if (_pageDetail is not null)
            {
                _pageDetail.Text = _localizer.T("launcher.wizard.vm.paused_no_space_details",
                    Args(("drive", _activeVmUsbRoot?.TrimEnd('\\') ?? "USB")));
            }

            ConfigureActions(false, _localizer.T("launcher.wizard.vm.resume"), true, PrimaryMark.Play);
            return;
        }

        ShowVmRunningAgain();
    }

    private async Task ResumeVmAsync()
    {
        if (_dependencies.ResumeDexTor is not { } resume) return;
        SetBusy(true);
        try
        {
            if (!await resume()) return;
        }
        finally
        {
            SetBusy(false);
        }

        _vmPausedForSpace = false;
        ShowVmRunningAgain();
    }

    private void ShowVmRunningAgain()
    {
        if (_pageStatus is not null)
        {
            _pageStatus.Text = _localizer.T("launcher.wizard.vm.running");
            _pageStatus.ForeColor = WizardDesign.Text;
        }

        if (_pageDetail is not null) _pageDetail.Text = _localizer.T("launcher.wizard.vm.running_details");
        ConfigureActions(false, _localizer.T("launcher.wizard.vm.stop"), true, PrimaryMark.None);
    }

    /// <summary>
    /// Starts DexTor OS, first saying so when the stick has too little room left for the
    /// storage to grow to its full size: it then pauses the moment the room runs out.
    /// </summary>
    private async Task StartVmCheckedAsync(string usbRoot)
    {
        var shortfall = _dependencies.StorageShortfall?.Invoke(usbRoot) ?? 0;
        if (shortfall > 0 && !AskConfirmation(this, _localizer,
                _localizer.T("launcher.wizard.vm.low_space_confirm", Args(
                    ("drive", usbRoot.TrimEnd('\\')),
                    ("missing", FormatBytes(shortfall)))),
                _localizer.T("launcher.wizard.vm.low_space_start"),
                _localizer.T("launcher.wizard.vm.low_space_cancel"),
                destructive: false,
                ConfirmationStyle.Abort))
        {
            return;
        }

        await StartVmAsync(usbRoot);
    }

    internal Task CheckActiveVmUsbNowAsync()
    {
        if (!_vmRunning
            || _vmStopRequested
            || _emergencyShutdownInProgress
            || string.IsNullOrWhiteSpace(_activeVmUsbRoot)
            || _dependencies.IsActiveUsbAvailable(_activeVmUsbRoot))
        {
            return Task.CompletedTask;
        }

        return EmergencyShutdownAfterUsbRemovalAsync();
    }

    private async Task EmergencyShutdownAfterUsbRemovalAsync()
    {
        if (_emergencyShutdownInProgress) return;
        _emergencyShutdownInProgress = true;
        _vmStateTimer.Stop();
        _vmRunning = false;
        _vmHasRun = false;
        _vmPausedForSpace = false;
        _activeVmUsbRoot = null;
        Hide();

        try
        {
            await _dependencies.EmergencyStopDexTor();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            // USB removal is already unsafe; continue closing the UI even if QEMU exited first.
        }

        try
        {
            await _dependencies.ShowEmergencyNotification(
                _localizer.T("launcher.wizard.vm.usb_removed_title"),
                _localizer.T("launcher.wizard.vm.usb_removed_message"));
        }
        finally
        {
            Close();
        }
    }

    private static string SessionStageKey(SessionStartStage stage) => stage switch
    {
        SessionStartStage.CheckingUsb => "launcher.wizard.vm.checking_usb",
        SessionStartStage.CheckingRuntime => "launcher.wizard.vm.checking_runtime",
        SessionStartStage.LockingStorage => "launcher.wizard.vm.locking_storage",
        SessionStartStage.StartingQemu => "launcher.wizard.vm.starting",
        SessionStartStage.ConnectingControlChannel => "launcher.wizard.vm.connecting",
        SessionStartStage.GuestFirmware or SessionStartStage.GuestLoadingSystem or SessionStartStage.GuestShowing => "launcher.wizard.vm.starting",
        _ => "launcher.wizard.vm.running"
    };

    private async Task HandleHostPrimaryAsync()
    {
        if (_environment is null)
        {
            await ShowHostCheckAsync();
            return;
        }

        if (_restartPending)
        {
            _dependencies.RestartWindows();
            return;
        }

        if (!_environment.Preflight.CanContinueWithWarnings)
        {
            await ShowHostCheckAsync();
            return;
        }

        if (_environment.Context.NeedsReboot)
        {
            _dependencies.RestartWindows();
            return;
        }

        var whpxWarning = _environment.Preflight.Requirements.Any(r =>
            r.Kind == PreflightRequirementKind.HostVirtualization && r.State == PreflightRequirementState.Warning);
        // Without virtualization the primary action is "Create the USB drive only".
        _usbOnly = whpxWarning;

        if (_existingUsbVerified && !whpxWarning)
        {
            await ShowExistingUsbReadyAsync();
            return;
        }

        await ShowImageAsync();
    }

    private void GoBack()
    {
        if (_busy)
        {
            return;
        }

        switch (_step)
        {
            case LauncherWizardStep.HostCheck:
                ShowIntro();
                break;
            case LauncherWizardStep.Image:
                if (_environment is not null)
                {
                    SetStep(LauncherWizardStep.HostCheck);
                    var panel = CreatePage(_localizer.T("launcher.wizard.host.title"), _localizer.T("launcher.wizard.host.description"));
                    RenderHostResult(panel, _environment.Preflight);
                }
                break;
            case LauncherWizardStep.Usb:
                if (_preparedImage?.Success == true)
                {
                    RenderImageReady();
                }
                else
                {
                    _ = ShowImageAsync();
                }
                break;
        }
    }

    private string? _pendingLanguage;

    /// <summary>Gives every control that uses one of the wizard's own fonts the face of the new language.</summary>
    private static void RestyleFonts(Control control)
    {
        if (WizardFonts.Restyle(control.Font) is { } font) control.Font = font;
        foreach (Control child in control.Controls) RestyleFonts(child);
    }

    private void ChangeLanguage(string language)
    {
        if (_localizer is not Localizer localizer)
        {
            return;
        }

        if (_busy)
        {
            // The list stays usable while the wizard works, as in the design; the page
            // changes language once the work is done.
            _pendingLanguage = language;
            return;
        }

        // The whole window is blended, not only the page: the header and the buttons
        // change language too, and they must not flip ahead of the content.
        BeginTransition(_root);
        localizer.SetLanguage(language);
        if (WizardFonts.UseLanguage(language)) RestyleFonts(this);
        Text = _localizer.T("launcher.wizard.window_title");
        foreach (var step in Steps)
        {
            _stepLabels[step].Text = _localizer.T(StepLabelKey(step));
        }

        _closeButton.Text = _localizer.T("launcher.close");
        _closeButton.FitWidth();
        foreach (var badge in Controls.Find("WizardBetaBadge", true)) badge.Text = _localizer.T("launcher.wizard.badge_beta");
        _copyright.Text = LauncherBranding.FooterText(_localizer);
        _actionBar.PerformLayout();

        switch (_step)
        {
            case LauncherWizardStep.Intro:
                ShowIntro();
                break;
            case LauncherWizardStep.HostCheck when _environment is not null:
                SetStep(LauncherWizardStep.HostCheck);
                var hostPage = CreatePage(_localizer.T("launcher.wizard.host.title"), _localizer.T("launcher.wizard.host.description"));
                RenderHostResult(hostPage, _environment.Preflight);
                break;
            case LauncherWizardStep.Image:
                RenderCurrentImagePage();
                break;
            case LauncherWizardStep.Usb:
                // Re-render what is already known. Asking Windows again would drop the
                // list back to "searching" just because the language changed.
                var selected = (_usbList?.SelectedItem as UsbListItem)?.Snapshot;
                BuildUsbPage(selected?.PhysicalIdentity?.UniqueId, selected?.RootPath);
                if (!_usbDiscoveryCompleted)
                {
                    _ = RunGuardedAsync(RefreshUsbCandidatesAsync);
                }
                break;
            case LauncherWizardStep.Create when _lastCreateResult is { Success: false }:
                ShowCreateFailed(_lastCreateResult);
                break;
            case LauncherWizardStep.Complete when _lastCreateResult is not null:
                ShowComplete(_lastCreateResult);
                break;
            case LauncherWizardStep.Complete:
                _ = ShowExistingUsbReadyAsync();
                break;
            default:
                ShowIntro();
                break;
        }
    }

    private void RenderCurrentImagePage()
    {
        if (_preparedImage?.Success == true)
        {
            RenderImageReady();
        }
        else if (_imageFailure is not null)
        {
            RenderImageFailure(_imageFailure);
        }
        else if (_imageInspection?.Success == true)
        {
            RenderImageDownloadRequired();
        }
        else
        {
            RenderImageInspectionFailed();
        }
    }

    // ------------------------------------------------------------------ page parts

    private WizardPage CreatePage(string title, string description)
    {
        BeginTransition(_contentHost);
        var previous = _contentHost.Controls.Cast<Control>().ToArray();
        _contentHost.Controls.Clear();
        foreach (var control in previous)
        {
            // Removed pages are disposed so their animation timers stop with them.
            control.Dispose();
        }
        var page = new WizardPage { Name = "WizardPage", Dock = DockStyle.Fill, Scrollable = true };
        AddHeading(page, title, description);
        // The thumb sits in the window's right margin, outside the page and its 880 px.
        _root.Controls.Add(page.Thumb);
        page.Thumb.BringToFront();
        _contentHost.Controls.Add(page);
        _pageStatus = null;
        _pageDetail = null;
        _pageProgress = null;
        _transferStats = null;
        _statusRow = null;
        return page;
    }

    private static void ClearPage(FlowLayoutPanel panel)
    {
        var previous = panel.Controls.Cast<Control>().ToArray();
        panel.Controls.Clear();
        foreach (var control in previous)
        {
            control.Dispose();
        }
    }

    /// <summary>The page title - 28/36, the product name in violet - and its 14/22 line under it.</summary>
    private static void AddHeading(FlowLayoutPanel page, string title, string description)
    {
        var titleLabel = new WizardText(28, 600, 36, WizardDesign.Text)
        {
            Name = "WizardPageTitle", Text = title, HighlightBrand = true, LetterSpacing = -0.01f,
            WrapWidth = 0, Margin = S(0, 1, 0, 0)
        };
        var descriptionLabel = new WizardText(14, 400, 22, WizardDesign.TextSecondary)
        {
            Name = "WizardPageDescription", Text = description, WrapWidth = S(ContentWidth), Margin = S(0, 9, 0, 0)
        };
        page.Controls.Add(titleLabel);
        page.Controls.Add(descriptionLabel);
    }

    /// <summary>Adds a block under the heading, <paramref name="gap"/> design pixels below what is above it.</summary>
    private static void AddToPage(FlowLayoutPanel page, Control control, float gap = 20)
    {
        // Only the gap above: a panel's default 3 px at the sides pushed it past the page
        // and brought up scroll bars.
        control.Margin = new Padding(0, S(gap), 0, 0);
        page.Controls.Add(control);
    }

    private static WizardInfoPanel CreateInfoPanel(params Control[] controls)
    {
        var panel = new WizardInfoPanel(S(ContentWidth));
        foreach (var control in controls)
        {
            panel.Controls.Add(control);
        }

        return panel;
    }

    private static WizardRow CreateRow(string name, WizardIcon icon, string title, string? detail)
        => new(S(RowWidth), icon, title, detail) { Name = name };

    private static GlassNotice CreateNotice(string name, Tone tone, WizardIcon icon, string text)
    {
        var notice = new GlassNotice(S(RowWidth)) { Name = name, Kind = tone, Icon = icon };
        notice.Text = text;
        return notice;
    }

    /// <summary>
    /// The design's status card: a caption, then a row with an icon tile, the status and its
    /// detail, then - where the page moves bytes - the transfer chart and figures and the
    /// progress bar, all inside the one card.
    /// </summary>
    private WizardInfoPanel CreateStatusCard(string caption, WizardIcon icon, string status, string detail, bool transfer, bool progress)
    {
        var card = CreateInfoPanel();
        card.Name = "WizardStatusCard";
        card.Controls.Add(CreateCaption(caption));
        var row = CreateRow("WizardStatusRow", icon, status, detail);
        row.TitleText.Name = "WizardStatusTitle";
        row.DetailText.Name = "WizardStatusDetail";
        _statusRow = row;
        _pageStatus = row.TitleText;
        _pageDetail = row.DetailText;
        card.Controls.Add(row);
        if (transfer)
        {
            _transferStats = new TransferStatsView(72) { Name = "WizardTransferStats", Width = S(RowWidth), Visible = false, Margin = S(0, 4, 0, 0) };
            card.Controls.Add(_transferStats);
        }

        if (progress)
        {
            _pageProgress = new WizardProgressBar { Name = "WizardProgressBar", Width = S(RowWidth), IsIndeterminate = true, Margin = S(0, transfer ? 12 : 16, 0, 0) };
            card.Controls.Add(_pageProgress);
        }

        _statusCard = card;
        return card;
    }

    private static string StepLabelKey(LauncherWizardStep step) => step switch
    {
        LauncherWizardStep.Intro => "launcher.wizard.step.intro",
        LauncherWizardStep.HostCheck => "launcher.wizard.step.host",
        LauncherWizardStep.Image => "launcher.wizard.step.image",
        LauncherWizardStep.Usb => "launcher.wizard.step.usb",
        LauncherWizardStep.Create => "launcher.wizard.step.create",
        _ => "launcher.wizard.step.complete"
    };

    private static GlassButton CreateButton(string text, ButtonKind kind, float minimumWidth)
    {
        var button = new GlassButton
        {
            Kind = kind,
            MinimumWidth = S(minimumWidth),
            Margin = Padding.Empty
        };
        button.Text = text;
        button.Width = Math.Max(button.Width, S(minimumWidth));
        button.FitWidth();
        return button;
    }

    private static Dictionary<string, string> Args(params (string Key, string Value)[] values)
    {
        return values.ToDictionary(value => value.Key, value => value.Value);
    }

    private string FormatBytes(long bytes) => FormatSize(bytes, _localizer);

    /// <summary>
    /// Every size the wizard shows, in one format: "57.8 GB", "16 GB", "245.8 MB" (the decimal separator follows the language) - one
    /// decimal, none when it is zero, as in the design.
    /// </summary>
    internal static string FormatSize(long bytes, ILocalizer localizer)
    {
        var units = localizer.T("launcher.unit.bytes").Split('|');
        return FormatBytes(bytes, units.Length == EnglishByteUnits.Length ? units : EnglishByteUnits, CultureFor(localizer));
    }

    private static CultureInfo CultureFor(ILocalizer localizer)
    {
        localizer.TryGetLanguage(out var language);
        try
        {
            return CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(language) ? "en" : language);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    private static string FormatBytes(long bytes, string[] units, IFormatProvider culture)
    {
        var value = (double)Math.Max(0, bytes);
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return string.Format(culture, "{0:0.#} {1}", value, units[index]);
    }

    private static readonly string[] EnglishByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary><paramref name="bytes"/> in the unit <paramref name="reference"/> is shown in: "0.8 GB" next to "2 GB".</summary>
    private string FormatBytesLike(long bytes, long reference)
    {
        var units = _localizer.T("launcher.unit.bytes").Split('|');
        if (units.Length != EnglishByteUnits.Length) units = EnglishByteUnits;
        var index = 0;
        for (var value = (double)Math.Max(1, reference); value >= 1024 && index < units.Length - 1; value /= 1024) index++;
        return string.Format(DisplayCulture, "{0:0.#} {1}", Math.Max(0, bytes) / Math.Pow(1024, index), units[index]);
    }

    /// <summary>Speed as file copies show it, in megabytes per second - for the network too, as in the design.</summary>
    private string FormatBytesPerSecond(double bytesPerSecond)
    {
        var unit = _localizer.T("launcher.unit.mb_per_second");
        if (unit == "launcher.unit.mb_per_second") unit = "MB/s";
        return string.Format(DisplayCulture, "{0:0.0} {1}", Math.Max(0, bytesPerSecond) / (1024d * 1024d), unit);
    }

    private string FormatNumber(double value, string format) => value.ToString(format, DisplayCulture);

    private CultureInfo DisplayCulture => CultureFor(_localizer);

    private static string DisplayDrive(UsbDriveSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.RootPath)
            ? snapshot.RootPath
            : snapshot.PhysicalIdentity is { } identity
                ? $"PhysicalDrive{identity.DiskNumber}"
                : "—";

    private static string FormatDuration(TimeSpan duration)
    {
        var safe = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
        return safe.TotalHours >= 1
            ? $"{(int)safe.TotalHours}:{safe.Minutes:00}:{safe.Seconds:00}"
            : $"{(int)safe.TotalMinutes}:{safe.Seconds:00}";
    }

    private static Bitmap? LoadEmbeddedBitmap(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return stream is null ? null : new Bitmap(stream);
    }

    private void TrySetWindowIcon()
    {
        using var bitmap = LoadEmbeddedBitmap("DexTor.Launcher.Assets.favicon.png");
        if (bitmap is null)
        {
            return;
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var sourceIcon = Icon.FromHandle(handle);
            Icon = (Icon)sourceIcon.Clone();
        }
        finally
        {
            _ = DestroyIcon(handle);
        }
    }

    /// <summary>
    /// Opaque, with the Mica-like wallpaper tint of <see cref="WizardDesign.Window"/>: at 96 %
    /// the desktop showed sharply through every card, which testers found too transparent.
    /// </summary>
    internal const double WindowOpacity = 1.0;

    private bool _revealed;

    /// <summary>
    /// The first time the window is shown it starts fully transparent.
    /// </summary>
    /// <remarks>
    /// A layered window is put on screen before its controls have painted, so testers saw
    /// white rectangles where the content would be, and a window that appeared in one place
    /// and then moved to the centre once its final size was known. It is now revealed in
    /// <see cref="OnShown"/>, centred and fully painted.
    /// </remarks>
    protected override void SetVisibleCore(bool value)
    {
        if (value && !_revealed && !DesignMode) Opacity = 0;
        base.SetVisibleCore(value);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_revealed) return;
        _revealed = true;
        if (StartPosition == FormStartPosition.CenterScreen && WindowState == FormWindowState.Normal)
        {
            var area = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(
                area.Left + Math.Max(0, (area.Width - Width) / 2),
                area.Top + Math.Max(0, (area.Height - Height) / 2));
        }

        Refresh();
        Opacity = WindowOpacity;
    }
    private static readonly Color SuccessColor = WizardDesign.GreenLight;
    private static readonly Color ErrorColor = WizardDesign.Error;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // DWM ignores unsupported attributes on older Windows versions.
        var dark = 1;
        _ = DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
        var rounded = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
        // The title bar takes the window's own colour (COLORREF is 0x00BBGGRR), so the
        // frame and the page read as one surface, as in the design.
        var window = WizardDesign.Window;
        var caption = window.B << 16 | window.G << 8 | window.R;
        _ = DwmSetWindowAttribute(Handle, 35, ref caption, sizeof(int));
        // The design's title bar carries only the window buttons: no icon, no title. Both
        // stay in the taskbar and Alt+Tab.
        var options = new WindowThemeOptions { Flags = 0x1 | 0x2, Mask = 0x1 | 0x2 };
        _ = SetWindowThemeAttribute(Handle, 1, ref options, (uint)Marshal.SizeOf<WindowThemeOptions>());
    }

    protected override void WndProc(ref Message message)
    {
        if (ShouldCancelAutoPlayMessage(message.Msg))
        {
            message.Result = new IntPtr(1);
            return;
        }

        base.WndProc(ref message);
        if (IsDeviceRemovalMessage(message.Msg, message.WParam))
        {
            _ = CheckActiveVmUsbNowAsync();
        }

        if (IsDeviceArrivalOrRemovalMessage(message.Msg, message.WParam) && _step == LauncherWizardStep.Usb)
        {
            // A stick plugged in while the USB step is open shows up as soon as Windows
            // has mounted it, instead of on the next five-second poll.
            _ = RunGuardedAsync(RefreshUsbAfterDeviceChangeAsync);
        }
    }

    private async Task RefreshUsbAfterDeviceChangeAsync()
    {
        await Task.Delay(700);
        await RefreshUsbCandidatesAsync();
    }

    internal static bool IsDeviceArrivalOrRemovalMessage(int message, IntPtr eventCode) =>
        message == 0x0219 && (eventCode == new IntPtr(0x8000) || eventCode == new IntPtr(0x8004));

    internal static bool ShouldCancelAutoPlayMessage(int message) =>
        QueryCancelAutoPlayMessage != 0 && message == QueryCancelAutoPlayMessage;

    internal static bool IsDeviceRemovalMessage(int message, IntPtr eventCode) =>
        message == 0x0219 && (eventCode == new IntPtr(0x8003) || eventCode == new IntPtr(0x8004));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _usbRefreshTimer.Dispose();
            _vmStateTimer.Dispose();
            if (_tray is { } tray)
            {
                _tray = null;
                tray.Visible = false;
                tray.ContextMenuStrip?.Dispose();
                tray.Dispose();
            }
            FinishTransition();
            _transitionTimer?.Dispose();
            _activeOperationCancellation?.Dispose();
        }

        base.Dispose(disposing);
    }

    private sealed record UsbListItem(UsbDriveSnapshot Snapshot)
    {
        public override string ToString()
        {
            var device = string.IsNullOrWhiteSpace(Snapshot.RootPath)
                ? Snapshot.PhysicalIdentity is { } identity
                    ? $"Disk {identity.DiskNumber}  {identity.FriendlyName}".TrimEnd()
                    : "USB"
                : Snapshot.RootPath;
            var label = string.IsNullOrWhiteSpace(Snapshot.VolumeLabel) ? string.Empty : $"  {Snapshot.VolumeLabel}";
            var capacity = Snapshot.TotalSizeBytes is { } size
                ? $"  ·  {FormatBytes(size, EnglishByteUnits, CultureInfo.InvariantCulture)}"
                : string.Empty;
            return $"{device}{label}{capacity}";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowThemeOptions
    {
        public uint Flags;
        public uint Mask;
    }

    [LibraryImport("uxtheme.dll")]
    private static partial int SetWindowThemeAttribute(IntPtr window, int attribute, ref WindowThemeOptions options, uint size);

    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(IntPtr handle);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegisterWindowMessage(string message);
}
