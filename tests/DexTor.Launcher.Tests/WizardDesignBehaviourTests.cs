using System.Drawing;
using System.Reflection;
using System.Windows.Forms;

using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.SessionHost;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// What the wizard does on the designer's screens (2026-10) beyond how it looks: the three
/// download failures, "create the USB drive only", the external-disk confirmation, the way
/// back while this PC is checked, and the language list that stays usable while it works.
/// </summary>
public sealed class WizardDesignBehaviourTests
{
    private static readonly long GB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(LiveImagePreparationService.DownloadFailedKey, "launcher.wizard.image.failed_download_title", "launcher.wizard.image.retry", true)]
    [InlineData(LiveImagePreparationService.ChecksumFailedKey, "launcher.wizard.image.failed_checksum_title", "launcher.wizard.image.download_again", true)]
    [InlineData(LiveImagePreparationService.NoSpaceKey, "launcher.wizard.image.failed_space_title", "launcher.wizard.image.retry", false)]
    public async Task EachDownloadFailureHasItsOwnBlockAndNextStep(string key, string title, string action, bool details)
    {
        var args = key == LiveImagePreparationService.NoSpaceKey
            ? new Dictionary<string, string> { ["drive"] = "C:", ["needed_bytes"] = (2 * GB).ToString(System.Globalization.CultureInfo.InvariantCulture), ["available_bytes"] = "858993459" }
            : null;
        using var window = new LauncherWizardWindow(new KeyLocalizer(), Dependencies(
            prepare: (_, _) => Task.FromResult(new LiveImagePreparationResult(false, null, null, null, 0, "socket closed", key, args))));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.Image);
        Glyph(window, "WizardStepGlyphImage").Text.Should().Be("✕", "the step that failed turns red in the stepper");
        AllText(window).Should().Contain(text => text.StartsWith(title, StringComparison.Ordinal));
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be(action);
        (window.Controls.Find("WizardDetailsToggle", true).Length > 0).Should().Be(details, "the technical reason is one click away where there is one");
    }

    [Fact]
    public async Task RetryingAfterAFailedDownloadDownloadsAgain()
    {
        var attempts = 0;
        using var window = new LauncherWizardWindow(new KeyLocalizer(), Dependencies(
            prepare: (_, _) =>
            {
                attempts++;
                return Task.FromResult(attempts == 1
                    ? new LiveImagePreparationResult(false, null, null, null, 0, "offline", LiveImagePreparationService.DownloadFailedKey)
                    : new LiveImagePreparationResult(true, null, @"C:\Temp\DexTor\LiveImage\dextor-os-1.0.img", new string('a', 64), GB));
            }));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        attempts.Should().Be(2);
        AllText(window).Should().Contain("launcher.wizard.image.verify_caption");
        // Only what is really checked is listed: the checksum, not a signature.
        AllText(window).Should().Contain("launcher.wizard.image.checksum");
        AllText(window).Should().Contain(text => text.Contains("DexTor OS 1.0", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSpaceCheckReportsTheDriveAndBothFigures()
    {
        var noRoom = LiveImagePreparationService.CheckSpace(@"C:\DexTor\LiveImage\image.part", 2 * GB, _ => 800L * 1024 * 1024);
        noRoom.Should().NotBeNull();
        noRoom!["drive"].Should().Be("C:");
        noRoom["needed_bytes"].Should().Be((2 * GB).ToString(System.Globalization.CultureInfo.InvariantCulture));
        noRoom["available_bytes"].Should().Be((800L * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));

        LiveImagePreparationService.CheckSpace(@"C:\DexTor\LiveImage\image.part", 2 * GB, _ => 10 * GB).Should().BeNull();
        LiveImagePreparationService.CheckSpace(@"C:\DexTor\LiveImage\image.part", 2 * GB, _ => null).Should().BeNull("an unreadable drive is not reported as full");
    }

    [Fact]
    public async Task WithoutVirtualizationTheUsbDriveCanBeCreatedOnly()
    {
        using var window = new LauncherWizardWindow(new KeyLocalizer(), Dependencies(virtualizationOff: true));
        window.CreateControl();

        await window.AdvanceAsync();

        AllText(window).Should().Contain("launcher.wizard.host.virtualization_off");
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.host.usb_only");
        window.Controls.Find("WizardBackButton", true).Single().Text.Should().Be("launcher.wizard.host.check_again");

        await window.AdvanceAsync();
        window.UsbOnly.Should().BeTrue();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.Complete);
        // Nothing will start from Windows: that way is not offered, and the page says why.
        window.Controls.Find("WizardNextWindows", true).Should().BeEmpty();
        window.Controls.Find("WizardNoVirtualizationNotice", true).Should().ContainSingle(string.Join(",", AllNames(window)));
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.complete.done");
        window.Controls.Find("WizardCloseButton", true).Single().Visible.Should().BeFalse("the design has the one button there");
        window.Controls.Find("WizardBackButton", true).Single().Visible.Should().BeFalse();
    }

    [Fact]
    public async Task HowToEnableVirtualizationRunsTheAutomaticPreparationAndAsksForARestart()
    {
        var prepared = 0;
        var restarts = 0;
        var dependencies = Dependencies(virtualizationOff: true);
        using var window = new LauncherWizardWindow(new KeyLocalizer(), new LauncherWizardDependencies
        {
            RefreshEnvironment = dependencies.RefreshEnvironment,
            PreparePc = () =>
            {
                prepared++;
                return Task.FromResult(new LauncherInlineActionResult(true, "Hyper-V platform enabled.", "Restart to finish."));
            },
            RestartWindows = () => ++restarts > 0,
            InspectImage = dependencies.InspectImage,
            PrepareImage = dependencies.PrepareImage,
            InspectUsbDrives = dependencies.InspectUsbDrives,
            HasEnoughSpace = dependencies.HasEnoughSpace,
            ConfirmErase = dependencies.ConfirmErase,
            CreateUsb = dependencies.CreateUsb,
            VerifyDexTorUsb = dependencies.VerifyDexTorUsb,
            StartDexTor = dependencies.StartDexTor,
            StopDexTor = dependencies.StopDexTor,
            EjectDexTor = dependencies.EjectDexTor,
            EmergencyStopDexTor = dependencies.EmergencyStopDexTor,
            IsActiveUsbAvailable = dependencies.IsActiveUsbAvailable,
            ShowEmergencyNotification = dependencies.ShowEmergencyNotification,
            GetSessionState = dependencies.GetSessionState,
            GetSessionError = dependencies.GetSessionError
        });
        window.CreateControl();
        await window.AdvanceAsync();

        var notice = window.Controls.Find("WizardHostSummary", true).Single();
        notice.GetType().GetMethod("PerformAction", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(notice, null);
        for (var i = 0; i < 10 && prepared == 0; i++) await Task.Delay(10);
        await Task.Delay(20);

        prepared.Should().Be(1);
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.primary_action_restart_required");
        await window.AdvanceAsync();
        restarts.Should().Be(1);
    }

    [Fact]
    public async Task GoingBackWhileThisPcIsCheckedIgnoresTheLateAnswer()
    {
        var answer = new TaskCompletionSource<LauncherWizardEnvironment>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new LauncherWizardWindow(new KeyLocalizer(), Dependencies(environment: () => answer.Task));
        window.CreateControl();

        var checking = window.AdvanceAsync();
        window.CurrentStep.Should().Be(LauncherWizardStep.HostCheck);
        var back = (Button)window.Controls.Find("WizardBackButton", true).Single();
        back.Enabled.Should().BeTrue();
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(back, [EventArgs.Empty]);
        await Task.Delay(20);
        window.CurrentStep.Should().Be(LauncherWizardStep.Intro);

        answer.SetResult(new LauncherWizardEnvironment(new PreflightReport(true, []), new LauncherContext { IsWhpxEnabled = true }));
        await checking;

        window.CurrentStep.Should().Be(LauncherWizardStep.Intro, "the answer belongs to a check the operator left");
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ALanguageChosenWhileTheWizardWorksIsAppliedWhenItIsDone()
    {
        var answer = new TaskCompletionSource<LauncherWizardEnvironment>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new LauncherWizardWindow(new Localizer("en"), Dependencies(environment: () => answer.Task));
        window.CreateControl();
        var checking = window.AdvanceAsync();

        var selector = (LauncherBranding.ThemedLanguageSelector)window.Controls.Find("DexTorLanguageSelector", true).Single();
        selector.Enabled.Should().BeTrue("the design keeps the list usable while the wizard checks");
        selector.SelectLanguage("ru");
        window.Text.Should().NotBe("Настройка DexTor OS", "the page is not rebuilt in the middle of the check");

        answer.SetResult(new LauncherWizardEnvironment(new PreflightReport(true, []), new LauncherContext { IsWhpxEnabled = true }));
        await checking;
        for (var i = 0; i < 5; i++)
        {
            System.Windows.Forms.Application.DoEvents();
            await Task.Delay(10);
        }

        window.Text.Should().Be("Настройка DexTor OS");
        window.CurrentStep.Should().Be(LauncherWizardStep.HostCheck);
    }

    [Fact]
    public async Task AnExternalDiskIsMarkedAndWarnedAbout()
    {
        var disk = new UsbDriveSnapshot(@"E:\", "WD", "NTFS", 2000L * GB, 1200L * GB, true, true,
            new UsbPhysicalIdentity(4, "id", "serial", 2000L * GB, "USB", "WD Elements", false, false, false), IsExternalDisk: true);
        using var window = new LauncherWizardWindow(new KeyLocalizer(), Dependencies(drives: [disk]));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.Usb);
        AllText(window).Should().Contain("launcher.wizard.usb.status_check");
        AllText(window).Should().Contain("launcher.wizard.usb.external_notice");
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue("it can still be used, once confirmed");
    }

    [Theory]
    [InlineData("External hard disk media", true)]
    [InlineData("Fixed hard disk media", true)]
    [InlineData("Removable Media", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void WindowsMediaTypeTellsAnExternalDiskFromAStick(string? mediaType, bool external)
    {
        UsbDiscoveryService.IsExternalDiskMedia(mediaType).Should().Be(external);
    }

    [Fact]
    public void AnExternalDiskIsErasedOnlyAfterTheAcknowledgementIsTicked()
    {
        using var dialog = new LauncherWizardWindow.ConfirmationDialog(new Localizer("en"),
            "Erase?\n\nDrive: E:\\\nModel: WD Elements\nCapacity: 1.8 TB\n\nEverything will be removed.", "Erase", "Cancel",
            destructive: true, acknowledgement: "I understand that all data will be erased");
        var accept = (Button)dialog.Controls.Find("ConfirmationAccept", true).Single();
        var tick = (CheckBox)dialog.Controls.Find("ConfirmationAcknowledge", true).Single();

        accept.Enabled.Should().BeFalse();
        tick.Checked = true;
        accept.Enabled.Should().BeTrue();
        ((Button)dialog.CancelButton!).DialogResult.Should().Be(DialogResult.No);
    }

    [Fact]
    public void StoppingAWriteAsksWithTheRedOutlinedStopAndTheGreenKeepGoing()
    {
        using var dialog = new LauncherWizardWindow.ConfirmationDialog(new Localizer("ru"),
            new Localizer("ru").T("launcher.wizard.create.abort_confirm"), "Прервать", "Продолжить запись",
            destructive: true, LauncherWizardWindow.ConfirmationStyle.Abort);
        var stop = (Button)dialog.Controls.Find("ConfirmationAccept", true).Single();
        var keep = (Button)dialog.Controls.Find("ConfirmationCancel", true).Single();

        stop.Left.Should().BeLessThan(keep.Left, "keeping on is the main action, on the right");
        dialog.Controls.Find("ConfirmationClose", true).Should().BeEmpty();
        dialog.AcceptButton.Should().Be(keep, "Enter keeps writing");
        dialog.Controls.Find("ConfirmationHeading", true).Single().Text.Should().Be("Прервать запись?");
    }

    [Fact]
    public void TheWizardIsSetInInter()
    {
        WizardFonts.InterLoaded.Should().BeTrue();
        using var font = WizardFonts.Get(14, 600);
        font.FontFamily.Name.Should().Be("Inter SemiBold");
    }

    [Theory]
    [InlineData(16L * 1024 * 1024 * 1024, "16 GB")]
    [InlineData(62_062_000_000L, "57.8 GB")]
    [InlineData(257_740_000L, "245.8 MB")]
    public void SizesHaveOneDecimalAndNoneWhenItIsZero(long bytes, string expected)
    {
        LauncherWizardWindow.FormatSize(bytes, new Localizer("en")).Should().Be(expected);
    }

    private static IEnumerable<string> AllNames(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child.Name;
            foreach (var name in AllNames(child)) yield return name;
        }
    }

    private static Label Glyph(Control root, string name) => (Label)root.Controls.Find(name, true).Single();

    private static IEnumerable<string> AllText(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child.Text;
            foreach (var text in AllText(child)) yield return text;
        }
    }

    private static LauncherWizardDependencies Dependencies(
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>>? prepare = null,
        bool virtualizationOff = false,
        Func<Task<LauncherWizardEnvironment>>? environment = null,
        IReadOnlyList<UsbDriveSnapshot>? drives = null)
    {
        var report = new PreflightReport(true, virtualizationOff
            ? [new PreflightRequirement(PreflightRequirementKind.HostVirtualization, PreflightRequirementState.Warning, "t", "d")]
            : []);
        var stick = new UsbDriveSnapshot(@"F:\", "", "exFAT", 64 * GB, 60 * GB, true, true,
            new UsbPhysicalIdentity(3, "id", "serial", 64 * GB, "USB", "Stick", false, false, false));
        var clean = new SessionFinalizationReport(
            GuestShutdown: GuestShutdownEvidence.ObservedGuestInitiated,
            Escalation: ShutdownEscalation.PowerButton,
            WriterTermination: WriterTerminationResult.Confirmed,
            Flush: HostFlushResult.Succeeded,
            Journal: JournalPersistenceResult.Persisted);
        return new LauncherWizardDependencies
        {
            RefreshEnvironment = environment ?? (() => Task.FromResult(new LauncherWizardEnvironment(report, new LauncherContext { IsWhpxEnabled = !virtualizationOff }))),
            PreparePc = () => Task.FromResult(new LauncherInlineActionResult(true, "Ready", "Restart")),
            RestartWindows = () => true,
            InspectImage = (_, _) => Task.FromResult(new LiveImageCacheInspectionResult(true, prepare is null, @"C:\Temp\DexTor\LiveImage", null,
                @"C:\Temp\DexTor\LiveImage\dextor-os-1.0.img", prepare is null ? new string('c', 64) : null, GB)),
            PrepareImage = prepare ?? ((_, _) => Task.FromResult(new LiveImagePreparationResult(true, null, @"C:\Temp\DexTor\LiveImage\dextor-os-1.0.img", new string('c', 64), GB))),
            InspectUsbDrives = _ => Task.FromResult(drives ?? [stick]),
            HasEnoughSpace = (_, _) => true,
            ConfirmErase = _ => true,
            CreateUsb = (drive, _) => Task.FromResult(new CreatorActionService.CreateUsbResult(true, drive.RootPath, new CreatorTransaction(CreatorStep.Complete))),
            VerifyDexTorUsb = (_, _, _) => Task.FromResult(new DexTorUsbReadinessResult(false)),
            StartDexTor = (_, _) => Task.CompletedTask,
            StopDexTor = _ => Task.FromResult(clean),
            EjectDexTor = (_, _) => Task.FromResult(clean),
            EmergencyStopDexTor = () => Task.CompletedTask,
            IsActiveUsbAvailable = _ => true,
            ShowEmergencyNotification = (_, _) => Task.CompletedTask,
            GetSessionState = () => SessionState.Running,
            GetSessionError = () => null
        };
    }

    /// <summary>Returns keys, so the tests read what the wizard asked for, not a translation.</summary>
    private sealed class KeyLocalizer : ILocalizer
    {
        public string T(string key, IReadOnlyDictionary<string, string>? args = null) => args is null || args.Count == 0
            ? key
            : key + " " + string.Join(" ", args.Values);

        public bool TryGetLanguage(out string language)
        {
            language = "en";
            return true;
        }
    }
}
