using System.Collections.Generic;

using DexTor.Application;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public class LauncherPreflightPresenterTests
{
    [Fact]
    public void BlockerCreatesRetryActionAndBlockedStatus()
    {
        var report = BuildReport(
            new PreflightRequirement(
                PreflightRequirementKind.Runtime,
                PreflightRequirementState.Blocker,
                "preflight.runtime.title",
                "preflight.runtime.blocker",
                ActionTextKey: "preflight.runtime.blocker.action",
                ActionUrl: "https://dotnet.microsoft.com/download"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer());

        model.ShouldExit.Should().BeTrue();
        model.CanContinue.Should().BeFalse();
        model.PrimaryAction.Should().Be(LauncherPrimaryAction.Retry);
        model.Requirements.Should().HaveCount(1);
        model.Requirements[0].ActionText.Should().Be("preflight.runtime.blocker.action");
        model.Requirements[0].SolutionText.Should().Be("preflight.solution.block");
        model.Header.Should().Be("launcher.blocked.title");
        model.SubHeader.Should().Be("launcher.blocked.details");
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_retry");
    }

    [Fact]
    public void WarningLeadsToPrepareAction()
    {
        var report = BuildReport(
            new PreflightRequirement(
                PreflightRequirementKind.HostVirtualization,
                PreflightRequirementState.Warning,
                "preflight.virtualization.title",
                "preflight.virtualization.warning"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer());

        model.Requirements[0].SolutionText.Should().Be("preflight.solution.warning");
        model.ShouldExit.Should().BeFalse();
        model.CanContinue.Should().BeTrue();
        model.PrimaryAction.Should().Be(LauncherPrimaryAction.Prepare);
        model.Header.Should().Be("launcher.warning");
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_prepare");
    }

    [Fact]
    public void ReportWithActionLinkSetsHasActionLink()
    {
        var report = BuildReport(
            new PreflightRequirement(
                PreflightRequirementKind.Runtime,
                PreflightRequirementState.Blocker,
                "preflight.runtime.title",
                "preflight.runtime.blocker",
                ActionTextKey: "preflight.runtime.blocker.action",
                ActionUrl: "https://dotnet.microsoft.com/en-us/download/dotnet/10.0"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer());

        model.HasActionLink.Should().BeTrue();
        model.Requirements.Should().ContainSingle();
        model.Requirements[0].ActionText.Should().NotBeNullOrWhiteSpace();
        model.Requirements[0].ActionUrl.Should().Be("https://dotnet.microsoft.com/en-us/download/dotnet/10.0");
    }

    [Fact]
    public void OkStateHasNoSolverSuggestion()
    {
        var report = BuildReport(
            new PreflightRequirement(
                PreflightRequirementKind.Runtime,
                PreflightRequirementState.Ok,
                "preflight.runtime.title",
                "preflight.runtime.ok"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer());

        model.Requirements[0].SolutionText.Should().BeEmpty();
    }

    [Fact]
    public void ReadyStateWithoutActionLinksUsesPrimaryContinue()
    {
        var report = BuildReport(
            new PreflightRequirement(
                PreflightRequirementKind.OperatingSystem,
                PreflightRequirementState.Ok,
                "preflight.os.title",
                "preflight.os.ok"),
            new PreflightRequirement(
                PreflightRequirementKind.ProcessorArchitecture,
                PreflightRequirementState.Ok,
                "preflight.arch.title",
                "preflight.arch.ok"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer());

        model.ShouldExit.Should().BeFalse();
        model.CanContinue.Should().BeTrue();
        model.PrimaryAction.Should().Be(LauncherPrimaryAction.Continue);
        model.HasActionLink.Should().BeFalse();
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_continue");
        model.Header.Should().Be("launcher.ready");
        model.SubHeader.Should().Be("launcher.ready.details");
        model.Requirements.Should().ContainSingle();
        model.Requirements[0].Title.Should().Be("preflight.compatibility.title");
        model.Requirements[0].Description.Should().Be("preflight.compatibility.ok");
    }

    [Fact]
    public void WindowsAndArchitectureFailuresShareOneCompatibilityCard()
    {
        var report = BuildReport(
            new PreflightRequirement(
                PreflightRequirementKind.OperatingSystem,
                PreflightRequirementState.Ok,
                "preflight.os.title",
                "preflight.os.ok"),
            new PreflightRequirement(
                PreflightRequirementKind.ProcessorArchitecture,
                PreflightRequirementState.Blocker,
                "preflight.arch.title",
                "preflight.arch.blocker"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer());

        model.Requirements.Should().ContainSingle();
        model.Requirements[0].State.Should().Be(PreflightRequirementState.Blocker);
        model.Requirements[0].Title.Should().Be("preflight.compatibility.title");
        model.Requirements[0].Description.Should().Be("preflight.arch.blocker");
        model.Requirements[0].SolutionText.Should().Be("preflight.solution.block");
    }

    [Fact]
    public void UsbNotConnectedYieldsContinueToImagePreparation()
    {
        var report = BuildReport();
        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = false,
            IsDexTorUsb = false
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.Continue);
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_continue");
    }

    [Fact]
    public void NormalUsbConnectedStillYieldsContinueToImagePreparation()
    {
        var model = LauncherPreflightPresenter.Build(BuildReport(), new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = false
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.Continue);
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_continue");
    }

    [Fact]
    public void DextorUsbConnectedYieldsStartPrimaryAction()
    {
        var report = BuildReport();
        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.StartDexTor);
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_start_dextor");
    }

    [Fact]
    public void DextorUsbWithOptionalUpdateShowsRepairAndOpen()
    {
        var report = BuildReport();
        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasOptionalUpdate = true,
            DextorRootPath = @"D:\USB"
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.RepairAndStart);
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_repair_and_start");
        model.SecondaryAction.Should().Be(LauncherPrimaryAction.OpenDexTor);
        model.SecondaryActionLabel.Should().Be("launcher.primary_action_open_dextor");
    }

    [Fact]
    public void InterruptedSetupYieldsContinueAndStartOver()
    {
        var report = BuildReport();
        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasSetupInterrupted = true
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.ContinueSetup);
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_continue_setup");
        model.SecondaryAction.Should().Be(LauncherPrimaryAction.StartOverSetup);
        model.SecondaryActionLabel.Should().Be("launcher.primary_action_start_over_setup");
    }

    [Fact]
    public void DexTorUsbReadyShowsOpenAsSecondaryAction()
    {
        var report = BuildReport();
        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            DextorRootPath = @"D:\USB"
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.StartDexTor);
        model.SecondaryAction.Should().Be(LauncherPrimaryAction.OpenDexTor);
        model.SecondaryActionLabel.Should().Be("launcher.primary_action_open_dextor");
    }

    [Fact]
    public void InterruptedSetupStillShowsPrepareWhenWarningsExist()
    {
        var report = BuildReport(new PreflightRequirement(
            PreflightRequirementKind.HostVirtualization,
            PreflightRequirementState.Warning,
            "preflight.virtualization.title",
            "preflight.virtualization.warning"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasSetupInterrupted = true
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.Prepare);
        model.SecondaryAction.Should().BeNull();
    }

    [Fact]
    public void LiveImageRequirementOmitsInfoAndImageLinkButKeepsCustomSolution()
    {
        var report = BuildReport(new PreflightRequirement(
            PreflightRequirementKind.LiveImage,
            PreflightRequirementState.Warning,
            "preflight.live_image.title",
            "preflight.live_image.warning",
            InfoTextKey: "launcher.preflight.live_image.unavailable",
            SolutionTextKey: "launcher.preflight.live_image.solution",
            ActionTextKey: "launcher.preflight.live_image.link",
            ActionUrl: "https://dextoros.com/public/1.1.29/DextorOS_1_1_29.iso"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer());

        var row = model.Requirements[0];
        row.State.Should().Be(PreflightRequirementState.Warning);
        row.InfoText.Should().BeNull();
        row.SolutionText.Should().Be("launcher.preflight.live_image.solution");
        row.ActionText.Should().BeNull();
        row.ActionUrl.Should().BeNull();
        model.HasActionLink.Should().BeFalse();
        row.ActionText.Should().BeNullOrWhiteSpace();
        row.ActionUrl.Should().BeNullOrWhiteSpace();
        model.CanContinue.Should().BeTrue();
        model.PrimaryAction.Should().Be(LauncherPrimaryAction.Continue);
    }

    [Fact]
    public void RestartRequiredShowsContinueAsSecondaryAction()
    {
        var report = BuildReport(
            new PreflightRequirement(
                PreflightRequirementKind.Runtime,
                PreflightRequirementState.Ok,
                "preflight.runtime.title",
                "preflight.runtime.ok"));

        var model = LauncherPreflightPresenter.Build(report, new TestLocalizer(), new LauncherContext
        {
            IsWhpxEnabled = false,
            NeedsReboot = true
        });

        model.PrimaryAction.Should().Be(LauncherPrimaryAction.RestartRequired);
        model.PrimaryActionLabel.Should().Be("launcher.primary_action_restart_required");
        model.SecondaryAction.Should().Be(LauncherPrimaryAction.Continue);
        model.SecondaryActionLabel.Should().Be("launcher.primary_action_continue");
    }

    private static PreflightReport BuildReport(params PreflightRequirement[] requirements)
    {
        var canStart = requirements.All(r => r.State != PreflightRequirementState.Blocker);
        return new PreflightReport(canStart, requirements);
    }

    private sealed class TestLocalizer : ILocalizer
    {
        public string T(string key, IReadOnlyDictionary<string, string>? args = null) => key;

        public bool TryGetLanguage(out string language)
        {
            language = "en";
            return true;
        }
    }
}
