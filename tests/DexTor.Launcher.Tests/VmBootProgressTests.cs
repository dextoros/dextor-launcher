using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class VmBootProgressTests
{
    [Theory]
    [InlineData(GuestScreen.TextMode, SessionStartStage.ConnectingControlChannel, SessionStartStage.GuestFirmware)]
    [InlineData(GuestScreen.Placeholder, SessionStartStage.GuestFirmware, SessionStartStage.GuestLoadingSystem)]
    [InlineData(GuestScreen.Blank, SessionStartStage.GuestFirmware, SessionStartStage.GuestLoadingSystem)]
    [InlineData(GuestScreen.Picture, SessionStartStage.GuestLoadingSystem, SessionStartStage.GuestShowing)]
    public void TheHiddenScreenTellsHowFarTheGuestHasGot(GuestScreen screen, SessionStartStage reached, SessionStartStage expected)
    {
        SessionHostService.BootStageOf(screen, reached).Should().Be(expected);
    }

    [Fact]
    public void QemusPlaceholderBeforeTheFirmwareSaysNothingYet()
    {
        SessionHostService.BootStageOf(GuestScreen.Placeholder, SessionStartStage.ConnectingControlChannel).Should().BeNull();
        SessionHostService.BootStageOf(GuestScreen.Unknown, SessionStartStage.GuestFirmware).Should().BeNull();
    }

    [Fact]
    public void TheBarMovesWithTimeButNeverRunsAheadOfTheGuest()
    {
        var boot = new VmBootProgress(TimeSpan.FromSeconds(60));
        boot.Observe(SessionStartStage.StartingQemu);

        // 50 s into a 60 s boot, but the guest is still in its firmware: the bar waits at
        // the end of that step's share instead of pretending.
        var stuck = boot.Fraction(TimeSpan.FromSeconds(50));
        stuck.Should().BeLessThan(0.3);

        boot.Observe(SessionStartStage.GuestLoadingSystem);
        var loading = boot.Fraction(TimeSpan.FromSeconds(51));
        loading.Should().BeGreaterThan(stuck).And.BeLessThan(0.97);

        boot.Observe(SessionStartStage.CheckingUsb);
        boot.Stage.Should().Be(2, "a step is never taken back");
        boot.Fraction(TimeSpan.FromSeconds(52)).Should().BeGreaterThanOrEqualTo(loading);
    }

    [Fact]
    public void ABootThatTakesLongerKeepsCreepingRatherThanStopping()
    {
        var boot = new VmBootProgress(TimeSpan.FromSeconds(40));
        boot.Observe(SessionStartStage.GuestLoadingSystem);
        var values = Enumerable.Range(30, 60).Select(second => boot.Fraction(TimeSpan.FromSeconds(second))).ToList();

        values.Should().BeInAscendingOrder();
        values[^1].Should().BeLessThan(0.97).And.BeGreaterThan(values[15]);
    }

    [Fact]
    public void TheLastBootTimeIsRememberedForTheNextEstimate()
    {
        VmBootProgress.RememberBoot(TimeSpan.FromSeconds(63));
        VmBootProgress.ReadLastBoot().Should().Be(TimeSpan.FromSeconds(63));
    }
}

public sealed class VmDisplayAndSignalTests
{
    [Theory]
    [InlineData(0, 0, 2560, 1400, 144, 34, 2560, 1366, 150)]
    [InlineData(0, 0, 1920, 1040, 96, 23, 1920, 1017, 100)]
    [InlineData(-1920, 0, 0, 1032, 120, 29, 1920, 1003, 125)]
    [InlineData(0, 0, 3840, 2088, 192, 45, 3840, 2043, 200)]
    public void TheGuestGetsThePictureAreaOfTheMaximizedWindowInPhysicalPixels(
        int left, int top, int right, int bottom, int dpi, int caption, int width, int height, int scale)
    {
        var display = VmDisplayPlanner.Plan(left, top, right, bottom, dpi, caption);

        display.Width.Should().Be(width);
        display.Height.Should().Be(height);
        display.ScalePercent.Should().Be(scale);
        (display.WorkLeft, display.WorkRight).Should().Be((left, right), "the window opens on the launcher's own monitor");
    }

    [Theory]
    [InlineData(100, 16, 1)]
    [InlineData(125, 80, 5)]
    [InlineData(150, 48, 3)]
    [InlineData(175, 112, 7)]
    [InlineData(200, 16, 2)]
    public void TheGuestSizeFollowsDexTorOsRulesSoNothingSkewsAndGnomeCanScale(int scale, int widthStep, int heightStep)
    {
        // The table DexTor OS gave: a width of 16 or virtio-gpu skews; both sides of the scale's numerator.
        VmDisplayPlanner.Steps(scale).Should().Be((widthStep, heightStep));
    }

    [Theory]
    [InlineData("frame", GuestSignal.Frame)]
    [InlineData(" DESKTOP \r", GuestSignal.Desktop)]
    public void DexTorOsSignalsAreOneWordALine(string line, GuestSignal expected)
    {
        GuestSignals.Parse(line).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("show")]
    [InlineData("frame; rm -rf /")]
    public void AnythingElseOnThePortIsIgnored(string line)
    {
        GuestSignals.Parse(line).Should().BeNull();
    }

    [Fact]
    public async Task ARestartForgetsTheBootThatEndedAndWaitsForTheNextFrame()
    {
        await using var signals = GuestSignals.Unconnected();
        signals.Handle("frame");
        signals.Handle("desktop");
        signals.Frame.IsCompleted.Should().BeTrue();

        signals.Restart();

        // The window is covered again until the new boot draws, and guarded until its desktop.
        signals.FrameReady.Should().BeFalse();
        signals.DesktopReady.Should().BeFalse();
        signals.Frame.IsCompleted.Should().BeFalse();
        signals.Desktop.IsCompleted.Should().BeFalse();

        signals.Handle("frame");
        signals.FrameReady.Should().BeTrue();
        signals.Frame.IsCompleted.Should().BeTrue();
        signals.Desktop.IsCompleted.Should().BeFalse();
        signals.Handle("desktop");
        signals.Desktop.IsCompleted.Should().BeTrue();
    }
}

public sealed class VmWindowFittingTests
{
    [Theory]
    [InlineData(1366, 705, 100, 6, 0)]
    [InlineData(2560, 1351, 125, 0, 1)]
    [InlineData(2560, 1377, 150, 16, 0)]
    [InlineData(1920, 1080, 150, 0, 0)]
    public void AWindowTheOperatorSizedIsMadeJustSmallEnoughForDexTorOs(int width, int height, int scale, int dx, int dy)
    {
        QemuWindowWatcher.Excess(width, height, VmDisplayPlanner.Steps(scale)).Should().Be((dx, dy));
    }
}
