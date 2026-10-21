using System.Linq;
using System.Collections.Generic;
using FluentAssertions;
using Xunit;

using DexTor.Platform.Windows;
using DexTor.Application;

namespace DexTor.Platform.Windows.Tests;

public class HostPreflightTests
{
    [Fact]
    public async Task MissingRuntimeBecomesBlockerAndIncludesDownloadLink()
    {
        var preflight = new TestablePreflightEvaluator
        {
            Runtime = new PreflightRequirement(
                PreflightRequirementKind.Runtime,
                PreflightRequirementState.Blocker,
                "preflight.runtime.title",
                "preflight.runtime.blocker",
                ActionTextKey: "preflight.runtime.blocker.action",
                ActionUrl: PreflightGuide.DotNetDownloadUrl)
        };

        var report = await preflight.EvaluateAsync();

        report.CanStart.Should().BeFalse();
        report.Requirements.Should().Contain(r => r.Kind == PreflightRequirementKind.Runtime && r.State == PreflightRequirementState.Blocker);
        report.Requirements.Should().Contain(r => r.ActionUrl == PreflightGuide.DotNetDownloadUrl);
    }

    [Fact]
    public async Task WarningsDoNotBlockStart()
    {
        var preflight = new TestablePreflightEvaluator
        {
            Virtualization = new PreflightRequirement(
                PreflightRequirementKind.HostVirtualization,
                PreflightRequirementState.Warning,
                "preflight.virtualization.title",
                "preflight.virtualization.warning",
                ActionTextKey: "launcher.primary_action_prepare")
        };

        var report = await preflight.EvaluateAsync();
        report.CanStart.Should().BeTrue();
        report.Requirements.Should().Contain(r => r.Kind == PreflightRequirementKind.HostVirtualization && r.State == PreflightRequirementState.Warning);
        report.Requirements.Should().Contain(r => r.ActionTextKey == "launcher.primary_action_prepare");
    }

    [Fact]
    public async Task HyperVWarningProvidesSetupDocumentationLink()
    {
        var preflight = new TestablePreflightEvaluator
        {
            Virtualization = new PreflightRequirement(
                PreflightRequirementKind.HostVirtualization,
                PreflightRequirementState.Warning,
                "preflight.virtualization.title",
                "preflight.virtualization.warning",
                ActionTextKey: "launcher.primary_action_prepare",
                ActionUrl: HostPreflightEvaluator.WhpxGuideLink)
        };

        var report = await preflight.EvaluateAsync();

        var requirement = report.Requirements.Single(r => r.Kind == PreflightRequirementKind.HostVirtualization);
        requirement.ActionUrl.Should().Be("https://learn.microsoft.com/en-us/dotnet/maui/android/emulator/troubleshooting?view=net-maui-10.0");
    }

    [Fact]
    public async Task LiveImageWarningDoesNotBlockPreflightWithoutSourceAction()
    {
        var preflight = new TestablePreflightEvaluator
        {
            LiveImage = new PreflightRequirement(
                PreflightRequirementKind.LiveImage,
                PreflightRequirementState.Warning,
                "preflight.live_image.title",
                "preflight.live_image.warning",
                InfoTextKey: "launcher.preflight.live_image.unavailable",
                SolutionTextKey: "launcher.preflight.live_image.solution")
        };

        var report = await preflight.EvaluateAsync();

        report.CanStart.Should().BeTrue();
        var liveImage = report.Requirements.Single(r => r.Kind == PreflightRequirementKind.LiveImage);
        liveImage.State.Should().Be(PreflightRequirementState.Warning);
        liveImage.ActionTextKey.Should().BeNullOrWhiteSpace();
        liveImage.ActionUrl.Should().BeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(16L, PreflightRequirementState.Ok)]
    [InlineData(7L, PreflightRequirementState.Warning)]
    [InlineData(4L, PreflightRequirementState.Blocker)]
    public async Task MemoryIsCheckedAndReportedWithItsSize(long gigabytes, PreflightRequirementState expected)
    {
        var report = await new TestablePreflightEvaluator { Memory = gigabytes * 1024 * 1024 * 1024 }.EvaluateAsync();

        var memory = report.Requirements.Single(r => r.Kind == PreflightRequirementKind.Memory);
        memory.State.Should().Be(expected);
        memory.DescriptionArgs!["bytes"].Should().Be((gigabytes * 1024 * 1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
        report.CanStart.Should().Be(expected != PreflightRequirementState.Blocker);
    }

    [Theory]
    [InlineData(100L, PreflightRequirementState.Ok)]
    [InlineData(6L, PreflightRequirementState.Warning)]
    [InlineData(2L, PreflightRequirementState.Blocker)]
    public async Task FreeSpaceThatStepOnePromisedIsChecked(long gigabytes, PreflightRequirementState expected)
    {
        var report = await new TestablePreflightEvaluator { Disk = ("D:", gigabytes * 1024 * 1024 * 1024) }.EvaluateAsync();

        var disk = report.Requirements.Single(r => r.Kind == PreflightRequirementKind.DiskSpace);
        disk.State.Should().Be(expected);
        disk.DescriptionArgs!["drive"].Should().Be("D:");
    }

    private sealed class TestablePreflightEvaluator : HostPreflightEvaluator
    {
        public PreflightRequirement Runtime { get; set; } = new PreflightRequirement(
            PreflightRequirementKind.Runtime,
            PreflightRequirementState.Ok,
            "preflight.runtime.title",
            "preflight.runtime.ok");

        public PreflightRequirement Os { get; set; } = new PreflightRequirement(
            PreflightRequirementKind.OperatingSystem,
            PreflightRequirementState.Ok,
            "preflight.os.title",
            "preflight.os.ok");

        public PreflightRequirement Architecture { get; set; } = new PreflightRequirement(
            PreflightRequirementKind.ProcessorArchitecture,
            PreflightRequirementState.Ok,
            "preflight.arch.title",
            "preflight.arch.ok");

        public PreflightRequirement Virtualization { get; set; } = new PreflightRequirement(
            PreflightRequirementKind.HostVirtualization,
            PreflightRequirementState.Ok,
            "preflight.virtualization.title",
            "preflight.virtualization.ok");

        public PreflightRequirement LiveImage { get; set; } = new PreflightRequirement(
            PreflightRequirementKind.LiveImage,
            PreflightRequirementState.Ok,
            "preflight.live_image.title",
            "preflight.live_image.ok",
            InfoTextKey: "launcher.preflight.live_image.resolved_url",
            InfoTextArgs: new Dictionary<string, string>
            {
                ["url"] = "https://dextoros.com/public/1.1.29/DextorOS_1_1_29.iso"
            });

        protected override PreflightRequirement CheckOs() => Os;
        protected override PreflightRequirement CheckArchitecture() => Architecture;
        protected override PreflightRequirement CheckRuntime() => Runtime;
        protected override PreflightRequirement CheckWhpx() => Virtualization;
        protected override PreflightRequirement CheckLiveImageSource() => LiveImage;
        public long? Memory { get; set; } = 16L * 1024 * 1024 * 1024;
        public (string Drive, long FreeBytes)? Disk { get; set; } = ("C:", 100L * 1024 * 1024 * 1024);
        protected override long? TotalMemoryBytes() => Memory;
        protected override (string Drive, long FreeBytes)? CacheDriveFreeSpace() => Disk;
    }
}
