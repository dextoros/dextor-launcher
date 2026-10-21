using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.SessionHost;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class LauncherWizardWindowTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void IntroRowsIdentifyTheirMatchingStepsWithoutSuggestingNavigation(string language)
    {
        var localizer = new Localizer(language);
        using var window = new LauncherWizardWindow(localizer, CreateDependencies());
        foreach (var (name, step) in new[] { ("pc", LauncherWizardStep.HostCheck), ("image", LauncherWizardStep.Image), ("usb", LauncherWizardStep.Usb) })
        {
            // The design shows no step number on the rows; the step is named for screen readers.
            var row = window.Controls.Find("WizardIntroRow_" + name, true).Single();
            var stepLabel = window.Controls.Find($"WizardStepLabel{step}", true).Single();
            row.AccessibleDescription.Should().Be(stepLabel.Text);
            row.TabStop.Should().BeFalse();
            window.Controls.Find("WizardIntroStep_" + name, true).Should().BeEmpty();
        }
        AllText(window).Should().NotContain("›");
    }

    [Fact]
    public async Task WindowIsOpaqueWithAMicaTintThroughHostPreflight()
    {
        // 96 % let the desktop show sharply through every card; testers asked for the
        // Windows look, and Mica - an opaque window tinted by the wallpaper - was chosen.
        using var window = new LauncherWizardWindow(new TestLocalizer(), CreateDependencies());
        window.Opacity.Should().Be(1.0);
        window.TransparencyKey.Should().Be(Color.Empty);
        window.BackColor.Should().Be(WizardDesign.Window);

        await window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.HostCheck);
        window.Opacity.Should().Be(1.0);
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public void TheMicaTintKeepsTheWindowDarkAndNearTheDesignColour()
    {
        var tinted = WizardDesign.MicaTint(Color.FromArgb(0x0E, 0x0F, 0x13));
        foreach (var (channel, design) in new[] { (tinted.R, 0x0E), (tinted.G, 0x0F), (tinted.B, 0x13) })
        {
            ((int)channel).Should().BeInRange(design, design + 21, "at most 8 % of a white wallpaper");
        }
    }

    [Fact]
    public void BackdropUsesUniformTintAcrossTheWholeWindow()
    {
        using var window = new LauncherWizardWindow(new TestLocalizer(), CreateDependencies());
        var backdrop = window.Controls.Find("WizardRoot", true).Single();
        using var bitmap = new Bitmap(backdrop.Width, backdrop.Height);
        using var graphics = Graphics.FromImage(bitmap);
        using var args = new PaintEventArgs(graphics, backdrop.ClientRectangle);
        var paint = backdrop.GetType().GetMethod("OnPaintBackground", BindingFlags.Instance | BindingFlags.NonPublic)!;
        paint.Invoke(backdrop, [args]);
        // The same opaque tint is used everywhere, so no zone reads as a separate band.
        for (var y = 20; y < bitmap.Height - 20; y += 20)
        for (var x = 40; x < bitmap.Width - 20; x += 20)
        {
            bitmap.GetPixel(x, y).ToArgb().Should().Be(window.BackColor.ToArgb());
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    [InlineData("de")]
    public void EraseConfirmationIsSplitIntoHeadingDeviceFactsAndConsequence(string language)
    {
        var localizer = new Localizer(language);
        var message = localizer.T("launcher.wizard.usb.confirm_erase", new Dictionary<string, string>
        {
            ["drive"] = @"G:\",
            ["disk"] = "2",
            ["model"] = "Kingston DataTraveler 3.0",
            ["capacity"] = "57.75 GB"
        });

        var (heading, facts, consequence) = LauncherWizardWindow.ConfirmationDialog.SplitMessage(message);

        heading.Should().EndWith("?");
        // As in the design: drive, model and size - the physical disk number is not shown.
        facts.Should().HaveCount(3);
        facts.Select(fact => fact.Value).Should().Equal(@"G:\", "Kingston DataTraveler 3.0", "57.75 GB");
        consequence.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void DestructiveConfirmationDefaultsToTheSafeAnswer()
    {
        using var dialog = new LauncherWizardWindow.ConfirmationDialog(
            new Localizer("en"), "Erase?\n\nDrive: G:\\\n\nEverything will be removed.", "Erase", "Cancel", destructive: true);

        ((Button)dialog.AcceptButton!).DialogResult.Should().Be(DialogResult.No);
        ((Button)dialog.CancelButton!).DialogResult.Should().Be(DialogResult.No);
        FindControls<Button>(dialog).Should().Contain(button => button.DialogResult == DialogResult.Yes && button.Text == "Erase");
    }

    [Fact]
    public async Task CopyingToTheStickShowsLiveBytesSpeedAndAChart()
    {
        var reported = new TaskCompletionSource();
        var finish = new TaskCompletionSource<CreatorActionService.CreateUsbResult>();
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                inspectUsb: () => [new UsbDriveSnapshot(@"E:\", "USB", "exFAT", 64L * 1024 * 1024 * 1024, 60L * 1024 * 1024 * 1024, true, true,
                    new UsbPhysicalIdentity(7, "id", "serial", 64L * 1024 * 1024 * 1024, "USB", "Stick", false, false, false))],
                create: (_, progress) =>
                {
                    var key = "launcher.wizard.create.provisioner_writingsystemimage";
                    progress.Report(new CreatorActionService.CreatorStepProgress(CreatorStep.WriteSystemImage, CreatorActionService.CreatorStepStatus.Info, key, BytesDone: 1L << 30, BytesTotal: 4L << 30));
                    reported.SetResult();
                    return finish.Task;
                }));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        var creating = window.AdvanceAsync();
        await reported.Task;
        for (var i = 0; i < 5; i++) { System.Windows.Forms.Application.DoEvents(); await Task.Delay(10); }

        var stats = window.Controls.Find("WizardTransferStats", true).Should().ContainSingle().Subject;
        // One counter for the line "Writing the image": the boot image and the copy for Windows.
        stats.Text.Should().Contain("launcher.wizard.create.bytes_progress").And.Contain("1 GB").And.Contain("8 GB");
        finish.SetResult(new CreatorActionService.CreateUsbResult(false, @"E:\", CreatorActionService.CreateTransaction(), "stop"));
        await creating;
    }
    [Fact]
    public void AStickPluggedInOrRemovedIsRecognizedFromTheDeviceBroadcast()
    {
        LauncherWizardWindow.IsDeviceArrivalOrRemovalMessage(0x0219, new IntPtr(0x8000)).Should().BeTrue();
        LauncherWizardWindow.IsDeviceArrivalOrRemovalMessage(0x0219, new IntPtr(0x8004)).Should().BeTrue();
        LauncherWizardWindow.IsDeviceArrivalOrRemovalMessage(0x0219, new IntPtr(0x0007)).Should().BeFalse();
        LauncherWizardWindow.IsDeviceArrivalOrRemovalMessage(0x000F, new IntPtr(0x8000)).Should().BeFalse();
    }

    [Fact]
    public async Task ChangingLanguageOnTheUsbStepDoesNotAskWindowsAgain()
    {
        var inspections = 0;
        using var window = new LauncherWizardWindow(
            new Localizer("en"),
            CreateDependencies(ReadyImageInspection(), inspectUsb: () =>
            {
                inspections++;
                return [new UsbDriveSnapshot(@"E:\", "MY USB", "exFAT", 64L * 1024 * 1024 * 1024, 48L * 1024 * 1024 * 1024, true, true)];
            }));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        var before = inspections;

        typeof(LauncherWizardWindow).GetMethod("ChangeLanguage", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, ["ru"]);

        inspections.Should().Be(before);
        window.CurrentStep.Should().Be(LauncherWizardStep.Usb);
        AllText(window).Should().Contain(text => text.Contains("MY USB", StringComparison.Ordinal));
        AllText(window).Should().Contain(text => text.Contains("Выберите USB-накопитель", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoPlaySuppressionDoesNotConsumeOrdinaryWindowMessages()
    {
        LauncherWizardWindow.ShouldCancelAutoPlayMessage(0).Should().BeFalse();
        LauncherWizardWindow.ShouldCancelAutoPlayMessage(0x000F).Should().BeFalse();
    }

    [Fact]
    public void OpensOnFriendlyIntroWithBrandingAndAllStepsVisible()
    {
        using var window = new LauncherWizardWindow(new TestLocalizer(), CreateDependencies());
        window.CreateControl();
        PerformLayoutRecursively(window);

        window.CurrentStep.Should().Be(LauncherWizardStep.Intro);
        // The design's 960 x 680 window: a 648 px client area under the caption.
        window.ClientSize.Should().Be(new Size(WizardDesign.S(960), WizardDesign.S(648)));
        window.FormBorderStyle.Should().Be(FormBorderStyle.FixedSingle);
        window.Controls.Find("WizardLogo", true).Should().ContainSingle()
            .Which.Should().BeOfType<PictureBox>().Which.Image.Should().NotBeNull();
        window.Icon.Should().NotBeNull();
        var header = window.Controls.Find("WizardHeader", true).Should().ContainSingle().Subject;
        var stepper = window.Controls.Find("WizardStepper", true).Should().ContainSingle().Subject;
        var stepperInHeader = header.RectangleToClient(stepper.RectangleToScreen(stepper.ClientRectangle));
        header.ClientRectangle.Contains(stepperInHeader).Should().BeTrue();
        Enum.GetValues<LauncherWizardStep>().Should().AllSatisfy(step =>
        {
            var glyph = window.Controls.Find($"WizardStepGlyph{step}", true).Should().ContainSingle().Subject;
            var label = window.Controls.Find($"WizardStepLabel{step}", true).Should().ContainSingle().Subject;
            label.Top.Should().BeGreaterThanOrEqualTo(0);
            label.Bottom.Should().BeLessThanOrEqualTo(label.Parent!.ClientSize.Height);
            // Each name sits under its circle, centred on it.
            label.Top.Should().BeGreaterThan(glyph.Top + glyph.Height / 2);
            Math.Abs((glyph.Left + glyph.Width / 2) - (label.Left + label.Width / 2)).Should().BeLessThanOrEqualTo(1);
            var labelInHeader = header.RectangleToClient(label.RectangleToScreen(label.ClientRectangle));
            // Names are centred on their circles; the outer ones may reach into the side margins.
            labelInHeader.Top.Should().BeGreaterThanOrEqualTo(0);
            labelInHeader.Bottom.Should().BeLessThanOrEqualTo(header.ClientSize.Height);
        });
        window.Controls.Find("WizardPageTitle", true).Single().BackColor.Should().Be(Color.Transparent);
        window.Controls.Find("WizardPageDescription", true).Single().BackColor.Should().Be(Color.Transparent);
        window.Controls.Find("WizardContentHost", true).Single().BackColor
            .Should().Be(window.Controls.Find("WizardPageTitle", true).Single().Parent!.BackColor);
        window.Controls.Find("DexTorBrandFooter", true).Should().ContainSingle();
        AllText(window).Should().Contain(text => text.Contains("launcher.wizard.intro.description", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("ru")]
    public void GlassIntroKeepsOverviewAndActionsInsideWindow(string language)
    {
        using var window = new LauncherWizardWindow(new Localizer(language), CreateDependencies());
        window.CreateControl();
        PerformLayoutRecursively(window);
        var output = Environment.GetEnvironmentVariable("DEXTOR_UI_CAPTURE");
        if (!string.IsNullOrWhiteSpace(output))
        {
            window.StartPosition = FormStartPosition.Manual;
            window.Location = new Point(-32000, -32000);
            window.Show();
            PerformLayoutRecursively(window);
            using var bitmap = new Bitmap(window.Width, window.Height);
            window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size));
            System.IO.Directory.CreateDirectory(output);
            bitmap.Save(System.IO.Path.Combine(output, $"wizard-glass-{language}.png"));
            System.IO.File.WriteAllLines(System.IO.Path.Combine(output, $"layout-{language}.txt"), FindControls<Control>(window).Select(c => $"{c.Name}: {c.Bounds} visible={c.Visible} text={c.Text}"));
        }
        var overview = window.Controls.Find("WizardIntroOverview", true).Single();
        var page = window.Controls.Find("WizardPage", true).Single();
        overview.Bottom.Should().BeLessThanOrEqualTo(page.ClientSize.Height, string.Join("; ", page.Controls.Cast<Control>().Select(c => $"{c.Name}: {c.Bounds}")) + string.Join("; ", overview.Controls.Cast<Control>().Select(c => $"{c.Name}: {c.Bounds}, preferred {c.PreferredSize}")));
        overview.Right.Should().BeLessThanOrEqualTo(page.ClientSize.Width);
        overview.Controls.Cast<Control>().Should().Contain(control => control.Name == "WizardEraseNotice");
        window.Controls.Find("WizardIntroRow_pc", true).Should().ContainSingle();
        window.Controls.Find("WizardIntroRow_image", true).Should().ContainSingle();
        window.Controls.Find("WizardIntroRow_usb", true).Should().ContainSingle();
        var close = window.Controls.Find("WizardCloseButton", true).Single();
        var next = window.Controls.Find("WizardPrimaryButton", true).Single();
        close.Right.Should().BeLessThan(next.Left);
        AllText(window).Should().NotContain(text => text.StartsWith("launcher.wizard.intro."));

    }

    [Fact]
    public void IntroCardsGrowToFitEveryTextBlock()
    {
        using var window = new LauncherWizardWindow(new TestLocalizer(), CreateDependencies());
        window.CreateControl();
        PerformLayoutRecursively(window);

        AssertInfoCardsFit(window);
    }

    [Fact]
    public void FooterLanguageSelectorUpdatesTheVisibleWizardWithoutRestart()
    {
        using var window = new LauncherWizardWindow(new Localizer("en"), CreateDependencies());
        window.CreateControl();
        var selector = window.Controls.Find("DexTorLanguageSelector", true).Single()
            .Should().BeOfType<LauncherBranding.ThemedLanguageSelector>().Subject;
        selector.LanguageCount.Should().Be(10);
        LauncherBranding.ThemedLanguageSelector.MenuDirection.Should().Be(ToolStripDropDownDirection.AboveRight,
            "the selector is in the footer; a list opening downwards ran into the window edge");
        selector.BackColor.Should().Be(WizardDesign.Secondary);

        selector.SelectLanguage("ru");

        window.Text.Should().Be("Настройка DexTor OS");
        window.Controls.Find("WizardPageTitle", true).Single().Text.Should().Be("Создайте USB-накопитель с DexTor OS");
        window.Controls.Find("WizardStepLabelIntro", true).Single().Text.Should().Be("Начало");
    }

    [Fact]
    public async Task CompleteSetupStaysInOneWindowAndMarksFinishedSteps()
    {
        var createCalls = 0;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                new LiveImageCacheInspectionResult(
                    true,
                    true,
                    @"C:\Temp\DexTor\LiveImage",
                    "https://example.test/private/source.iso",
                    @"C:\Temp\DexTor\LiveImage\latest.iso",
                    new string('a', 64),
                    1024),
                [@"E:\"],
                (drive, _) =>
                {
                    createCalls++;
                    return Task.FromResult(new CreatorActionService.CreateUsbResult(
                        true,
                        drive,
                        new CreatorTransaction(CreatorStep.Complete)));
                }));
        window.CreateControl();
        var initialHandle = window.Handle;
        var initialSize = window.Size;

        await window.AdvanceAsync();
        window.CurrentStep.Should().Be(LauncherWizardStep.HostCheck);
        await window.AdvanceAsync();
        window.CurrentStep.Should().Be(LauncherWizardStep.Image);
        AllText(window).Should().NotContain(text => text.Contains("example.test", StringComparison.OrdinalIgnoreCase));
        await window.AdvanceAsync();
        window.CurrentStep.Should().Be(LauncherWizardStep.Usb);
        await window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.Complete);
        window.Handle.Should().Be(initialHandle);
        window.Size.Should().Be(initialSize);
        createCalls.Should().Be(1);
        window.OwnedForms.Should().BeEmpty();
        window.Controls.Find("WizardStepGlyphIntro", true).Single().Text.Should().Be("✓");
        window.Controls.Find("WizardStepGlyphCreate", true).Single().Text.Should().Be("✓");
        window.Controls.Find("WizardStepGlyphComplete", true).Single().Text.Should().Be("✓", "a created stick finishes the last step too");
    }

    [Fact]
    public async Task AFullStickPausesDexTorOsAndContinueLetsItCarryOn()
    {
        var paused = false;
        var resumes = 0;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, progress) =>
                {
                    progress.Report(new SessionStartProgress(SessionStartStage.Running));
                    return Task.CompletedTask;
                },
                getSessionState: () => SessionState.Running,
                isVmPausedForSpace: () => paused,
                resumeDexTor: () =>
                {
                    resumes++;
                    paused = false;
                    return Task.FromResult(true);
                }));
        window.CreateControl();
        for (var step = 0; step < 5; step++) await window.AdvanceAsync();
        var observe = typeof(LauncherWizardWindow).GetMethod("ObserveVmPause", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // QEMU paused the VM on ENOSPC: not a hang, not a crash - the operator is asked for room.
        paused = true;
        observe.Invoke(window, null);
        AllText(window).Should().Contain("launcher.wizard.vm.paused_no_space");
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.vm.resume");

        await window.AdvanceAsync();

        resumes.Should().Be(1);
        AllText(window).Should().Contain("launcher.wizard.vm.running");
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.vm.stop");
    }

    [Fact]
    public async Task CompleteStepStartsAndStopsVmWithoutLeavingWizard()
    {
        var starts = 0;
        var stops = 0;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, progress) =>
                {
                    starts++;
                    progress.Report(new SessionStartProgress(SessionStartStage.Running));
                    return Task.CompletedTask;
                },
                stop: _ =>
                {
                    stops++;
                    return Task.FromResult(CleanStopReport);
                }));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        starts.Should().Be(1);
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.vm.stop");
        await window.AdvanceAsync();
        stops.Should().Be(1);

        // Stopping does not eject. The device is still there, so the wizard offers
        // removal as a separate action rather than performing it unasked.
        window.EjectOffered.Should().BeTrue();
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.vm.eject");
    }

    [Fact]
    public async Task StoppingNeverEjectsAndEjectingIsASeparateAction()
    {
        var stops = 0;
        var ejects = 0;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, progress) =>
                {
                    progress.Report(new SessionStartProgress(SessionStartStage.Running));
                    return Task.CompletedTask;
                },
                stop: _ =>
                {
                    stops++;
                    return Task.FromResult(CleanStopReport);
                },
                eject: (_, _) =>
                {
                    ejects++;
                    return Task.FromResult(CleanStopReport with
                    {
                        DeviceRemoval = DeviceRemovalResult.Removed
                    });
                }));
        window.CreateControl();

        for (var step = 0; step < 5; step++)
        {
            await window.AdvanceAsync();
        }

        await window.AdvanceAsync();
        stops.Should().Be(1);
        ejects.Should().Be(0, "stopping the session must never remove the device on its own");

        await window.AdvanceAsync();

        ejects.Should().Be(1);
        window.EjectOffered.Should().BeFalse("Windows removed the device, so there is nothing left to eject");
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.complete.finish");
    }

    [Fact]
    public async Task AVetoedRemovalKeepsTheActionAvailableAndKeepsTheWarningVisible()
    {
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, progress) =>
                {
                    progress.Report(new SessionStartProgress(SessionStartStage.Running));
                    return Task.CompletedTask;
                },
                stop: _ => Task.FromResult(CleanStopReport with { Escalation = ShutdownEscalation.JobTerminated }),
                eject: (_, _) => Task.FromResult(CleanStopReport with
                {
                    Escalation = ShutdownEscalation.JobTerminated,
                    DeviceRemoval = DeviceRemovalResult.Vetoed,
                    DeviceRemovalDetail = "veto type 5"
                })));
        window.CreateControl();

        for (var step = 0; step < 5; step++)
        {
            await window.AdvanceAsync();
        }

        await window.AdvanceAsync();
        await window.AdvanceAsync();

        // A rejected request stays rejected: retry is offered, nothing is forced, and the
        // forced-shutdown warning is not quietly dropped to make the screen look better.
        window.EjectOffered.Should().BeTrue();
        window.PageDetailText.Should().Contain("launcher.session.forced_stop");
        window.PageDetailText.Should().Contain("launcher.session.removal_vetoed");
    }

    [Fact]
    public async Task StartDexTorShowsPatienceMessageBeforeBackgroundWorkCompletes()
    {
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new LauncherWizardWindow(
            new Localizer("ru"),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, _) => startup.Task));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        var start = window.AdvanceAsync();
        await Task.Yield();

        AllText(window).Should().Contain("Запускаем DexTor OS");
        AllText(window).Should().Contain(text => text.Contains("наберитесь терпения", StringComparison.Ordinal));
        PerformLayoutRecursively(window);
        AssertInfoCardsFit(window);
        var progress = typeof(LauncherWizardWindow).GetField("_pageProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        // A bar that moves with the boot, and the steps, instead of a sweep that looked like a hang.
        ((bool)progress.GetType().GetProperty("IsIndeterminate")!.GetValue(progress)!).Should().BeFalse();
        AllText(window).Should().Contain(text => text.Contains("Запуск виртуального компьютера", StringComparison.Ordinal)
            && text.Contains("Загрузка DexTor OS", StringComparison.Ordinal) && text.Contains("Открытие окна DexTor OS", StringComparison.Ordinal));
        AllText(window).Should().Contain(text => text.StartsWith("Прошло", StringComparison.Ordinal));

        startup.SetResult();
        await start;
    }

    [Fact]
    public async Task TheLaunchPageFollowsTheGuestThroughItsBoot()
    {
        IProgress<SessionStartProgress>? report = null;
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new LauncherWizardWindow(
            new Localizer("ru"),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, progress) =>
                {
                    report = progress;
                    return startup.Task;
                }));
        window.CreateControl();
        for (var step = 0; step < 4; step++) await window.AdvanceAsync();
        var start = window.AdvanceAsync();
        // The start runs off the UI thread; wait until it has been handed the progress.
        for (var i = 0; i < 200 && report is null; i++) { System.Windows.Forms.Application.DoEvents(); await Task.Delay(10); }

        report!.Report(new SessionStartProgress(SessionStartStage.GuestLoadingSystem));
        for (var i = 0; i < 5; i++) { System.Windows.Forms.Application.DoEvents(); await Task.Delay(10); }

        var stages = window.Controls.Find("WizardLaunchStages", true).Single();
        ((int)stages.GetType().GetProperty("Current", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stages)!).Should().Be(2);
        AllText(window).Should().Contain(text => text.Contains("Окно откроется само", StringComparison.Ordinal));

        startup.SetResult();
        await start;
    }

    [Fact]
    public async Task StartFailureKeepsTheDiagnosticVisibleInsideItsCard()
    {
        const string diagnostic = "The installed QEMU runtime manifest could not be validated, so DexTor OS was not started.";
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, _) => Task.FromException(new InvalidOperationException(diagnostic))));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        await window.AdvanceAsync();
        PerformLayoutRecursively(window);

        // The friendly line is in the block; the diagnostic stays one click away, under "Details".
        AllText(window).Should().Contain(text => text.Contains(diagnostic, StringComparison.Ordinal));
        AllText(window).Should().Contain("launcher.wizard.vm.failed");
        AssertInfoCardsFit(window);
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ClosingQemuStopsProgressAndAllowsSessionRestart()
    {
        var state = SessionState.Running;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, progress) =>
                {
                    progress.Report(new SessionStartProgress(SessionStartStage.Running));
                    return Task.CompletedTask;
                },
                getSessionState: () => state,
                getSessionError: () => "QEMU storage error: test failure"));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        state = SessionState.SafeToEject;
        typeof(LauncherWizardWindow).GetMethod("ObserveVmState", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, null);

        AllText(window).Should().Contain("launcher.wizard.vm.exited");
        AllText(window).Should().Contain(text => text.Contains("QEMU storage error: test failure", StringComparison.Ordinal));
        ((Control)typeof(LauncherWizardWindow).GetField("_pageProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!).Visible.Should().BeFalse();
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.primary_action_start_dextor");
    }

    [Fact]
    public async Task RemovingActiveUsbEmergencyStopsVmNotifiesAndClosesLauncher()
    {
        var emergencyStops = 0;
        var notifications = 0;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                start: (_, progress) =>
                {
                    progress.Report(new SessionStartProgress(SessionStartStage.Running));
                    return Task.CompletedTask;
                },
                emergencyStop: () =>
                {
                    emergencyStops++;
                    return Task.CompletedTask;
                },
                isActiveUsbAvailable: _ => false,
                showEmergencyNotification: (_, _) =>
                {
                    notifications++;
                    return Task.CompletedTask;
                }));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        await window.CheckActiveVmUsbNowAsync();

        emergencyStops.Should().Be(1);
        notifications.Should().Be(1);
        window.IsDisposed.Should().BeTrue();
    }

    [Theory]
    [InlineData(0x0219, 0x8003, true)]
    [InlineData(0x0219, 0x8004, true)]
    [InlineData(0x0219, 0x8000, false)]
    [InlineData(0x000F, 0x8004, false)]
    public void DeviceRemovalMessagesAreRecognized(int message, int eventCode, bool expected)
    {
        LauncherWizardWindow.IsDeviceRemovalMessage(message, new IntPtr(eventCode)).Should().Be(expected);
    }

    [Fact]
    public async Task HostCheckShowsStyledProgressAndExplainsChecksBeforeResult()
    {
        var completion = new TaskCompletionSource<LauncherWizardEnvironment>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(refreshEnvironment: () => completion.Task));
        window.CreateControl();

        var advance = window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.HostCheck);
        var progress = window.Controls.Find("WizardProgressBar", true).Should().ContainSingle().Subject;
        progress.BackColor.Should().NotBe(Color.Transparent);
        var card = window.Controls.Find("WizardHostChecking", true).Single();
        progress.Width.Should().Be(card.Width - card.Padding.Horizontal, "the bar spans the card, as the rows do");
        window.UseWaitCursor.Should().BeFalse();
        FindControls<ProgressBar>(window).Should().BeEmpty();
        // The design's checking state: the first check running, the others waiting.
        AllText(window).Should().Contain("launcher.wizard.host.status_checking");
        AllText(window).Should().Contain("launcher.wizard.host.status_waiting");
        window.Controls.Find("WizardBackButton", true).Single().Enabled.Should().BeTrue("the design keeps the way back open while checking");
        window.Controls.Find("WizardPage", true).Single().Should().BeOfType<LauncherWizardWindow.WizardPage>()
            .Which.Scrollable.Should().BeFalse();

        completion.SetResult(new LauncherWizardEnvironment(
            new PreflightReport(true, []),
            new LauncherContext { IsWhpxEnabled = true }));
        await advance;
        var page = window.Controls.Find("WizardPage", true).Single().Should().BeOfType<LauncherWizardWindow.WizardPage>().Subject;
        page.Scrollable.Should().BeTrue();
        page.AutoScroll.Should().BeFalse("the page scrolls itself; the system bars narrowed it and pulled in a horizontal bar");
        page.VerticalScroll.Visible.Should().BeFalse();
        page.HorizontalScroll.Visible.Should().BeFalse();
    }

    [Fact]
    public async Task ReadyExistingDexTorUsbSkipsDirectlyToCompleteAfterHostCheck()
    {
        var verificationCalls = 0;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                refreshEnvironment: () => Task.FromResult(new LauncherWizardEnvironment(
                    new PreflightReport(true, []),
                    new LauncherContext
                    {
                        IsWhpxEnabled = true,
                        IsDexTorUsb = true,
                        DextorRootPath = @"E:\"
                    })),
                verifyDexTorUsb: (_, progress, _) =>
                {
                    verificationCalls++;
                    progress.Report(new DexTorUsbVerificationProgress(1024, 1024));
                    return Task.FromResult(new DexTorUsbReadinessResult(true));
                }));
        window.CreateControl();

        await window.AdvanceAsync();

        verificationCalls.Should().Be(1);
        window.CurrentStep.Should().Be(LauncherWizardStep.Complete);
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.primary_action_start_dextor");
        window.Controls.Find("WizardStepGlyphImage", true).Single().Text.Should().Be("✓");
        window.Controls.Find("WizardStepGlyphCreate", true).Single().Text.Should().Be("✓");
    }

    [Fact]
    public async Task InvalidExistingDexTorUsbContinuesThroughNormalPreparationFlow()
    {
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                refreshEnvironment: () => Task.FromResult(new LauncherWizardEnvironment(
                    new PreflightReport(true, []),
                    new LauncherContext
                    {
                        IsWhpxEnabled = true,
                        IsDexTorUsb = true,
                        DextorRootPath = @"E:\"
                    })),
                verifyDexTorUsb: (_, _, _) => Task.FromResult(new DexTorUsbReadinessResult(false, "hash mismatch"))));
        window.CreateControl();

        await window.AdvanceAsync();
        window.CurrentStep.Should().Be(LauncherWizardStep.HostCheck);

        await window.AdvanceAsync();
        window.CurrentStep.Should().Be(LauncherWizardStep.Image);
    }

    [Fact]
    public async Task MissingImageExplainsDownloadWithoutShowingSourceAddress()
    {
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(new LiveImageCacheInspectionResult(
                true,
                false,
                @"C:\Temp\DexTor\LiveImage",
                "https://example.test/private/source.iso",
                @"C:\Temp\DexTor\LiveImage\latest.iso",
                null)));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.Image);
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.image_setup.download");
        AllText(window).Should().Contain("launcher.image_setup.download_required");
        AllText(window).Should().NotContain(text => text.Contains("example.test", StringComparison.OrdinalIgnoreCase));
        PerformLayoutRecursively(window);
        AssertInfoCardsFit(window);
    }

    [Fact]
    public async Task FailedCreationStaysOnTheCreateStepAndStartOverReturnsToTheDrives()
    {
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                new LiveImageCacheInspectionResult(
                    true,
                    true,
                    @"C:\Temp\DexTor\LiveImage",
                    null,
                    @"C:\Temp\DexTor\LiveImage\latest.iso",
                    new string('b', 64),
                    1024),
                [@"E:\"],
                (drive, _) => Task.FromResult(new CreatorActionService.CreateUsbResult(
                    false,
                    drive,
                    new CreatorTransaction(CreatorStep.FormatUsb, true),
                    "USB connection was lost."))));
        window.CreateControl();
        var initialHandle = window.Handle;

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        // The failure is shown where it happened: the creation step, marked red.
        window.CurrentStep.Should().Be(LauncherWizardStep.Create);
        AllText(window).Should().Contain("launcher.wizard.create.failed_title");
        window.Controls.Find("WizardPrimaryButton", true).Single().Text.Should().Be("launcher.wizard.complete.start_over");

        await window.AdvanceAsync();

        // "Start again" keeps the downloaded image and goes back to choosing the drive.
        window.CurrentStep.Should().Be(LauncherWizardStep.Usb);
        window.Handle.Should().Be(initialHandle);
        window.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public async Task DownloadProgressKeepsCardWidthAndShowsSpeedAndEta()
    {
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(new LiveImageCacheInspectionResult(
                true,
                false,
                @"C:\Temp\DexTor\LiveImage",
                null,
                null,
                null)));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        PerformLayoutRecursively(window);
        var card = window.Controls.Find("WizardStatusCard", true).Single();

        typeof(LauncherWizardWindow).GetField("_acceptImageProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);
        typeof(LauncherWizardWindow).GetMethod("UpdateImageProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new LiveImagePreparationProgress(
                128 * 1024 * 1024,
                2L * 1024 * 1024 * 1024,
                8d * 1024 * 1024,
                TimeSpan.FromMinutes(4),
                LiveImagePreparationService.StageDownload)]);
        PerformLayoutRecursively(window);

        card.Width.Should().Be(WizardDesign.S(880));
        // As in the design, the download speed is in MB/s. With a single sample the rolling
        // speed is not known yet, so the service's figure and ETA are used.
        AllText(window).Should().Contain(text =>
            text.Contains("8.0 MB/s", StringComparison.Ordinal)
            && text.Contains("128 MB", StringComparison.Ordinal)
            && text.Contains("2 GB", StringComparison.Ordinal)
            && text.Contains("4:00", StringComparison.Ordinal));
        var stats = window.Controls.Find("WizardTransferStats", true).Should().ContainSingle().Subject;
        stats.Width.Should().Be(WizardDesign.S(848), "the figures sit inside the card, as in the design");
        window.Controls.Find("WizardProgressBar", true).Should().ContainSingle();
        FindControls<ProgressBar>(window).Should().BeEmpty();
    }

    [Fact]
    public async Task ImageVerificationShowsBytesCheckedInsteadOfAFrozenBar()
    {
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(new LiveImageCacheInspectionResult(true, false, @"C:\Temp\DexTor\LiveImage", null, null, null)));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        typeof(LauncherWizardWindow).GetField("_acceptImageProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(window, true);
        typeof(LauncherWizardWindow).GetMethod("UpdateImageProgress", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [new LiveImagePreparationProgress(
                1024L * 1024 * 1024,
                2L * 1024 * 1024 * 1024,
                400d * 1024 * 1024,
                TimeSpan.FromSeconds(3),
                LiveImagePreparationService.StageVerifySha256)]);

        AllText(window).Should().Contain(text =>
            text.StartsWith("launcher.wizard.image.verify_progress", StringComparison.Ordinal)
            && text.Contains("50", StringComparison.Ordinal)
            && text.Contains("1 GB", StringComparison.Ordinal));
        window.Controls.Find("WizardTransferStats", true).Single().Visible.Should().BeFalse();
    }

    /// <summary>Whether the control itself is set visible, whatever its unshown window.</summary>
    private static bool DeclaredVisible(Control control)
    {
        // Control.GetState(States.Visible): what Visible would say if the window were shown.
        var getState = typeof(Control).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == "GetState" && method.GetParameters().Length == 1);
        var flag = getState.GetParameters()[0].ParameterType;
        return (bool)getState.Invoke(control, [flag.IsEnum ? Enum.ToObject(flag, 0x2) : 0x2])!;
    }

    [Fact]
    public async Task CancelDownloadUsesCancellationTokenAndKeepsWizardOpen()
    {
        var cancellableTokenWasUsed = false;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                new LiveImageCacheInspectionResult(
                    true,
                    false,
                    @"C:\Temp\DexTor\LiveImage",
                    null,
                    null,
                    null),
                prepare: async (token, _) =>
                {
                    cancellableTokenWasUsed = token.CanBeCanceled;
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new LiveImagePreparationResult(false, null, null, null);
                }));
        window.CreateControl();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        var download = window.AdvanceAsync();
        var cancel = window.Controls.Find("WizardBackButton", true).Single().Should().BeAssignableTo<Button>().Subject;
        cancel.Text.Should().Be("launcher.wizard.image.cancel");
        DeclaredVisible(window.Controls.Find("WizardPrimaryButton", true).Single())
            .Should().BeFalse("a greyed-out \"Downloading...\" button is not an action");
        cancel.Width.Should().BeGreaterThanOrEqualTo(180);
        cancel.Enabled.Should().BeTrue();
        InvokeButtonClick(cancel);
        await download;

        cancellableTokenWasUsed.Should().BeTrue();
        window.CurrentStep.Should().Be(LauncherWizardStep.Image);
        window.IsDisposed.Should().BeFalse();
        AllText(window).Should().Contain("launcher.wizard.image.cancelled");
        DeclaredVisible(window.Controls.Find("WizardPrimaryButton", true).Single()).Should().BeTrue();
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task UsbStepShowsDriveDetailsAndRefreshesEveryFiveSeconds()
    {
        IReadOnlyList<UsbDriveSnapshot> snapshots = [];
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(ReadyImageInspection(), inspectUsb: () => snapshots));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.Usb);
        window.UsbRefreshIntervalMilliseconds.Should().Be(5000);
        // Nothing connected: the design's empty card says what to connect.
        AllText(window).Should().Contain(text => text.Contains("launcher.wizard.usb.empty_title", StringComparison.Ordinal));
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeFalse();

        snapshots =
        [
            new UsbDriveSnapshot(
                @"E:\",
                "MY USB",
                "exFAT",
                64L * 1024 * 1024 * 1024,
                48L * 1024 * 1024 * 1024,
                true,
                true)
        ];
        await window.RefreshUsbNowAsync();

        // The check card shows the drive's facts in columns, as in the design.
        window.Controls.Find("WizardUsbFacts", true).Single().AccessibleName.Should().Contain("64 GB");
        AllText(window).Should().NotContain("launcher.wizard.usb.auto_refresh", "the polling interval is not the operator's concern");
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task UsbStepShowsSearchingStateUntilWindowsAnswers()
    {
        var discovery = new TaskCompletionSource<IReadOnlyList<UsbDriveSnapshot>>();
        var dependencies = CreateDependencies(ReadyImageInspection());
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            new LauncherWizardDependencies
            {
                RefreshEnvironment = dependencies.RefreshEnvironment,
                PreparePc = dependencies.PreparePc,
                RestartWindows = dependencies.RestartWindows,
                InspectImage = dependencies.InspectImage,
                PrepareImage = dependencies.PrepareImage,
                InspectUsbDrives = _ => discovery.Task,
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
        await window.AdvanceAsync();

        var entering = window.AdvanceAsync();

        window.CurrentStep.Should().Be(LauncherWizardStep.Usb);
        window.UsbSearching.Should().BeTrue();
        window.Controls.Find("WizardUsbList", true).Single().Visible.Should().BeFalse();
        AllText(window).Should().Contain("launcher.wizard.usb.searching");
        AllText(window).Should().NotContain("launcher.wizard.usb.none");

        discovery.SetResult([new UsbDriveSnapshot(@"E:\", "MY USB", "exFAT", 64L * 1024 * 1024 * 1024, 48L * 1024 * 1024 * 1024, true, true)]);
        await entering;

        window.UsbSearching.Should().BeFalse();
        AllText(window).Should().Contain(text => text.Contains("MY USB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UsbSpeedIsMeasuredAfterTheListIsShownAndFilledInWhenKnown()
    {
        var measurement = new TaskCompletionSource<UsbAccessSpeedResult?>();
        var snapshot = new UsbDriveSnapshot(
            @"E:\", "MY USB", "exFAT", 64L * 1024 * 1024 * 1024, 48L * 1024 * 1024 * 1024, true, true,
            new UsbPhysicalIdentity(7, "id", "serial", 64L * 1024 * 1024 * 1024, "USB", "Stick", false, false, false));
        var dependencies = CreateDependencies(ReadyImageInspection(), inspectUsb: () => [snapshot]);
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            new LauncherWizardDependencies
            {
                RefreshEnvironment = dependencies.RefreshEnvironment,
                PreparePc = dependencies.PreparePc,
                RestartWindows = dependencies.RestartWindows,
                InspectImage = dependencies.InspectImage,
                PrepareImage = dependencies.PrepareImage,
                InspectUsbDrives = dependencies.InspectUsbDrives,
                MeasureUsbSpeed = (_, _) => measurement.Task,
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
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue("creation does not wait for the speed");
        AllText(window).Should().Contain("launcher.wizard.usb.status_ready");

        // A measured stick that is fast enough adds nothing: the design's card shows the facts only.
        measurement.SetResult(new UsbAccessSpeedResult(21.5, 88.25));
        await Task.Yield();
        await Task.Delay(50);
        System.Windows.Forms.Application.DoEvents();

        AllText(window).Should().Contain("launcher.wizard.usb.status_ready");
        AllText(window).Should().NotContain(text => text.Contains("launcher.wizard.usb.speed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RawUsbIsExplainedAndCanBeSelectedForAutomaticPreparation()
    {
        var snapshots = new[]
        {
            new UsbDriveSnapshot(
                string.Empty,
                string.Empty,
                string.Empty,
                64L * 1024 * 1024 * 1024,
                null,
                false,
                true,
                new UsbPhysicalIdentity(7, "usb-id", "serial", 64L * 1024 * 1024 * 1024, "USB", "Raw USB", false, false, false))
        };
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(ReadyImageInspection(), inspectUsb: () => snapshots));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        AllText(window).Should().Contain(text => text.Contains("launcher.wizard.usb.needs_formatting", StringComparison.Ordinal));
        AllText(window).Should().Contain(text => text.Contains("Raw USB", StringComparison.Ordinal));
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task SlowUsbShowsPerformanceWarningWithoutBlockingCreation()
    {
        var snapshot = new UsbDriveSnapshot(
            @"E:\", "USB", "exFAT", 16_000_000_000, 15_000_000_000, true, true,
            new UsbPhysicalIdentity(7, "id", "serial", 16_000_000_000, "USB", "Slow USB", false, false, false),
            new UsbAccessSpeedResult(4.5, 18.2));
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(ReadyImageInspection(), inspectUsb: () => [snapshot]));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        AllText(window).Should().Contain("launcher.wizard.usb.status_slow");
        AllText(window).Should().Contain(text =>
            text.Contains("launcher.wizard.usb.speed_slow", StringComparison.Ordinal));
        window.Controls.Find("WizardPrimaryButton", true).Single().Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task UsbCreationRequiresExplicitEraseConfirmation()
    {
        var confirmationCalls = 0;
        var createCalls = 0;
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                ReadyImageInspection(),
                [@"E:\"],
                (_, _) =>
                {
                    createCalls++;
                    return Task.FromResult(new CreatorActionService.CreateUsbResult(
                        true,
                        @"E:\",
                        new CreatorTransaction(CreatorStep.Complete)));
                },
                confirmErase: _ =>
                {
                    confirmationCalls++;
                    return false;
                }));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        confirmationCalls.Should().Be(1);
        createCalls.Should().Be(0);
        window.CurrentStep.Should().Be(LauncherWizardStep.Usb);
    }

    [Fact]
    public async Task MarketedSixteenGbUsbIsAcceptedUsingTotalCapacity()
    {
        const long usbCapacity = 15_500_000_000;
        const long imageSize = 2_845_000_000;
        var confirmations = 0;
        var snapshot = new UsbDriveSnapshot(
            @"E:\",
            "USB 16GB",
            "exFAT",
            usbCapacity,
            1_000_000,
            true,
            true,
            new UsbPhysicalIdentity(7, "usb-id", "serial", usbCapacity, "USB", "16 GB USB", false, false, false));
        using var window = new LauncherWizardWindow(
            new TestLocalizer(),
            CreateDependencies(
                new LiveImageCacheInspectionResult(
                    true,
                    true,
                    @"C:\Temp\DexTor\LiveImage",
                    null,
                    @"C:\Temp\DexTor\LiveImage\latest.iso",
                    new string('c', 64),
                    imageSize),
                create: (drive, _) => Task.FromResult(new CreatorActionService.CreateUsbResult(
                    true,
                    drive,
                    new CreatorTransaction(CreatorStep.Complete))),
                inspectUsb: () => [snapshot],
                confirmErase: _ =>
                {
                    confirmations++;
                    return true;
                }));
        window.CreateControl();

        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();
        await window.AdvanceAsync();

        confirmations.Should().Be(1);
        window.CurrentStep.Should().Be(LauncherWizardStep.Complete);
        AllText(window).Should().NotContain("launcher.wizard.usb.no_space");
    }

    /// <summary>
    /// A host-finalized stop with no guest agent: the production shape of a good stop.
    /// </summary>
    private static SessionFinalizationReport CleanStopReport { get; } = new(
        GuestShutdown: GuestShutdownEvidence.ObservedGuestInitiated,
        Escalation: ShutdownEscalation.PowerButton,
        WriterTermination: WriterTerminationResult.Confirmed,
        Flush: HostFlushResult.Succeeded,
        Journal: JournalPersistenceResult.Persisted);

    internal static LauncherWizardDependencies MinimalDependencies() => CreateDependencies();

    private static LauncherWizardDependencies CreateDependencies(
        LiveImageCacheInspectionResult? inspection = null,
        string[]? usbCandidates = null,
        Func<string, IProgress<CreatorActionService.CreatorStepProgress>, Task<CreatorActionService.CreateUsbResult>>? create = null,
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>>? prepare = null,
        Func<IReadOnlyList<UsbDriveSnapshot>>? inspectUsb = null,
        Func<Task<LauncherWizardEnvironment>>? refreshEnvironment = null,
        Func<string, IProgress<DexTorUsbVerificationProgress>, CancellationToken, Task<DexTorUsbReadinessResult>>? verifyDexTorUsb = null,
        Func<UsbDriveSnapshot, bool>? confirmErase = null,
        Func<string, IProgress<SessionStartProgress>, Task>? start = null,
        Func<IProgress<SessionFinalizationStage>?, Task<SessionFinalizationReport>>? stop = null,
        Func<string, IProgress<SessionFinalizationStage>?, Task<SessionFinalizationReport>>? eject = null,
        Func<Task>? emergencyStop = null,
        Func<string, bool>? isActiveUsbAvailable = null,
        Func<string, string, Task>? showEmergencyNotification = null,
        Func<SessionState>? getSessionState = null,
        Func<string?>? getSessionError = null,
        Func<bool>? isVmPausedForSpace = null,
        Func<Task<bool>>? resumeDexTor = null)
    {
        inspection ??= new LiveImageCacheInspectionResult(
            true,
            false,
            @"C:\Temp\DexTor\LiveImage",
            null,
            null,
            null);
        return new LauncherWizardDependencies
        {
            RefreshEnvironment = refreshEnvironment ?? (() => Task.FromResult(new LauncherWizardEnvironment(
                new PreflightReport(true, []),
                new LauncherContext { IsWhpxEnabled = true }))),
            PreparePc = () => Task.FromResult(new LauncherInlineActionResult(true, "Ready", "Restart")),
            RestartWindows = () => true,
            InspectImage = (_, _) => Task.FromResult(inspection),
            PrepareImage = prepare ?? ((_, _) => Task.FromResult(new LiveImagePreparationResult(
                true,
                inspection.SourceUrl,
                inspection.LocalImagePath,
                inspection.SourceSha256,
                inspection.SourceSizeBytes))),
            InspectUsbDrives = _ => Task.FromResult<IReadOnlyList<UsbDriveSnapshot>>((inspectUsb?.Invoke() ?? (usbCandidates ?? [])
                .Select(path => new UsbDriveSnapshot(path, string.Empty, "exFAT", 64L * 1024 * 1024 * 1024, 48L * 1024 * 1024 * 1024, true, true))
                .ToArray())),
            HasEnoughSpace = (_, _) => true,
            ConfirmErase = confirmErase ?? (_ => true),
            CreateUsb = (snapshot, progress) => create is not null
                ? create(snapshot.RootPath, progress)
                : Task.FromResult(new CreatorActionService.CreateUsbResult(
                true,
                snapshot.RootPath,
                new CreatorTransaction(CreatorStep.Complete))),
            VerifyDexTorUsb = verifyDexTorUsb ?? ((_, _, _) => Task.FromResult(new DexTorUsbReadinessResult(false))),
            StartDexTor = start ?? ((_, _) => Task.CompletedTask),
            StopDexTor = stop ?? (_ => Task.FromResult(CleanStopReport)),
            EjectDexTor = eject ?? ((_, _) => Task.FromResult(CleanStopReport)),
            EmergencyStopDexTor = emergencyStop ?? (() => Task.CompletedTask),
            IsActiveUsbAvailable = isActiveUsbAvailable ?? (_ => true),
            ShowEmergencyNotification = showEmergencyNotification ?? ((_, _) => Task.CompletedTask),
            GetSessionState = getSessionState ?? (() => SessionState.Running),
            GetSessionError = getSessionError ?? (() => null),
            IsVmPausedForSpace = isVmPausedForSpace,
            ResumeDexTor = resumeDexTor
        };
    }

    private static LiveImageCacheInspectionResult ReadyImageInspection()
    {
        return new LiveImageCacheInspectionResult(
            true,
            true,
            @"C:\Temp\DexTor\LiveImage",
            null,
            @"C:\Temp\DexTor\LiveImage\latest.iso",
            new string('c', 64),
            1024);
    }

    private static IEnumerable<string> AllText(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child.Text;
            foreach (var text in AllText(child))
            {
                yield return text;
            }
        }
    }

    private static IEnumerable<TControl> FindControls<TControl>(Control root)
        where TControl : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is TControl match)
            {
                yield return match;
            }

            foreach (var descendant in FindControls<TControl>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PerformLayoutRecursively(Control root)
    {
        foreach (Control child in root.Controls)
        {
            PerformLayoutRecursively(child);
        }

        root.PerformLayout();
    }

    private static void AssertInfoCardsFit(Control root)
    {
        foreach (var card in root.Controls.Find("WizardInfoPanel", true))
        {
            var requiredBottom = card.Controls.Cast<Control>()
                .Select(control => control.Bottom + control.Margin.Bottom)
                .DefaultIfEmpty(card.Padding.Top)
                .Max() + card.Padding.Bottom;
            card.Height.Should().BeGreaterThanOrEqualTo(requiredBottom);
        }
    }

    private static void InvokeButtonClick(Button button)
    {
        var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        onClick.Should().NotBeNull();
        onClick!.Invoke(button, [EventArgs.Empty]);
    }

    private sealed class TestLocalizer : ILocalizer
    {
        public string T(string key, IReadOnlyDictionary<string, string>? args = null)
        {
            return args is null || args.Count == 0
                ? key
                : key + " " + string.Join(" ", args.Values);
        }

        public bool TryGetLanguage(out string language)
        {
            language = "en";
            return true;
        }
    }

    [Theory]
    [InlineData("en", "57.8 GB")]
    [InlineData("ru", "57,8 ГБ")]
    public void TheDriveCardAndTheEraseQuestionShowOneSizeTheSameWay(string language, string expected)
    {
        LauncherWizardWindow.FormatSize(62_008_590_336, new Localizer(language)).Should().Be(expected);
    }
}
