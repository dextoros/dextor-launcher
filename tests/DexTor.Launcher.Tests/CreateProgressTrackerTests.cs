using FluentAssertions;
using Xunit;

using DexTor.Contracts;
using DexTor.Creator;

namespace DexTor.Launcher.Tests;

public sealed class CreateProgressTrackerTests
{
    private const long Image = 1_600L * 1024 * 1024;
    private TimeSpan _now;
    private readonly List<TimeSpan> _times = [];

    private static CreateProgressTracker.Snapshot Report(CreateProgressTracker tracker, UsbProvisioningStage stage, long done = 0, long total = 0) =>
        tracker.Observe(new CreatorActionService.CreatorStepProgress(
            UsbProvisionerClient.MapStage(stage), CreatorActionService.CreatorStepStatus.Info,
            UsbProvisionerClient.StageMessageKey(stage), BytesDone: done, BytesTotal: total));

    /// <summary>
    /// The provisioner's run as it now goes: the boot image written, the data volume and the
    /// sparse storage, the copy for Windows written - and only then both read back from the stick.
    /// </summary>
    private List<CreateProgressTracker.Snapshot> WholeRun(CreateProgressTracker tracker, double writeMBps = 20, double readMBps = 40)
    {
        var seen = new List<CreateProgressTracker.Snapshot>();
        void Step(UsbProvisioningStage stage, long done = 0, long total = 0, double seconds = 2)
        {
            _now += TimeSpan.FromSeconds(seconds);
            _times.Add(_now);
            seen.Add(Report(tracker, stage, done, total));
        }

        void Pass(UsbProvisioningStage stage, long bytes, double mbps, long offset, long total)
        {
            const long chunk = 32L * 1024 * 1024;
            for (var done = 0L; done < bytes; done += chunk)
            {
                var step = Math.Min(chunk, bytes - done);
                Step(stage, offset + done + step, total, step / (mbps * 1024 * 1024));
            }
        }

        Step(UsbProvisioningStage.Validating, 0, Image);
        Step(UsbProvisioningStage.Validating, Image, Image);
        Step(UsbProvisioningStage.CleaningDisk, seconds: 8);
        Pass(UsbProvisioningStage.WritingSystemImage, Image, writeMBps, 0, Image);
        Step(UsbProvisioningStage.FormattingDataVolume, seconds: 5);
        Step(UsbProvisioningStage.WaitingForDataVolume);
        Step(UsbProvisioningStage.CreatingPrivateStorage, seconds: 1);
        Pass(UsbProvisioningStage.CopyingWindowsImage, Image, writeMBps, 0, Image);
        Pass(UsbProvisioningStage.Verifying, 2 * Image, readMBps, 0, 2 * Image);
        Step(UsbProvisioningStage.Complete);
        return seen;
    }

    [Fact]
    public void TheWholeRunIsOneRisingPercentageAndTheStepsGoInOrderWithoutCircling()
    {
        var seen = WholeRun(new CreateProgressTracker(() => _now));

        seen.Select(snapshot => snapshot.Overall).Should().BeInAscendingOrder();
        seen.Last().Overall.Should().Be(1);
        seen.Select(snapshot => snapshot.Phase).Should().BeInAscendingOrder();
        seen.Select(snapshot => snapshot.Phase).Distinct().Should().Equal(
            CreateProgressTracker.Phase.CheckingImage,
            CreateProgressTracker.Phase.ErasingUsb,
            CreateProgressTracker.Phase.WritingBootImage,
            CreateProgressTracker.Phase.CreatingDataVolume,
            CreateProgressTracker.Phase.CreatingStorage,
            CreateProgressTracker.Phase.CopyingWindowsImage,
            CreateProgressTracker.Phase.FinalCheck,
            CreateProgressTracker.Phase.Complete);
    }

    [Fact]
    public void TheBarMovesWithTheTimeTheRunTakesNotWithItsStages()
    {
        var seen = WholeRun(new CreateProgressTracker(() => _now));
        var length = _times[^1].TotalSeconds;

        foreach (var checkpoint in new[] { 0.25, 0.5, 0.75 })
        {
            var index = _times.FindIndex(at => at.TotalSeconds >= checkpoint * length);
            seen[index].Overall.Should().BeApproximately(checkpoint, 0.12, $"at {checkpoint:P0} of the time");
        }

        length.Should().BeLessThan(6 * 60, "a 1.6 GB image takes minutes");
    }

    [Fact]
    public void WritingTheImageIsOneCounterThatNeverDropsBackAndCheckingIsAnother()
    {
        // "Processed" fell back to 0 within "Writing the image" and its total went from 3.5 to
        // 1.7 GB; the read-back of the copy was reported at memory speed.
        var seen = WholeRun(new CreateProgressTracker(() => _now));
        var writing = seen.Where(snapshot => LauncherWizardWindow.CreateStageIndex(snapshot.Phase) == 1).ToList();
        var checking = seen.Where(snapshot => LauncherWizardWindow.CreateStageIndex(snapshot.Phase) == 2).ToList();

        writing.Select(snapshot => snapshot.StageBytesTotal).Distinct().Should().Equal(2 * Image);
        writing.Select(snapshot => snapshot.StageBytesDone).Should().BeInAscendingOrder();
        writing[^1].StageBytesDone.Should().Be(2 * Image);
        writing.Where(snapshot => snapshot.Speed != CreateProgressTracker.SpeedKind.None)
            .Should().OnlyContain(snapshot => snapshot.Speed == CreateProgressTracker.SpeedKind.Write);

        checking.Select(snapshot => snapshot.StageBytesTotal).Distinct().Should().Equal(2 * Image);
        checking.Select(snapshot => snapshot.StageBytesDone).Should().BeInAscendingOrder();
        checking.Should().OnlyContain(snapshot => snapshot.Speed == CreateProgressTracker.SpeedKind.Read);
    }

    [Fact]
    public void TimeLeftIsShownWithoutGapsFromTheFirstWriteToTheEnd()
    {
        var seen = WholeRun(new CreateProgressTracker(() => _now));
        var firstEstimate = seen.FindIndex(snapshot => snapshot.Remaining is not null);

        firstEstimate.Should().BeGreaterThan(0);
        seen.Skip(firstEstimate).TakeWhile(snapshot => snapshot.Phase != CreateProgressTracker.Phase.Complete)
            .Should().OnlyContain(snapshot => snapshot.Remaining != null,
                "the time left stays through formatting and through the check");
    }

    [Fact]
    public void CheckingTheWriteStartsAfterAllWritingAndRunsToTheEnd()
    {
        var seen = WholeRun(new CreateProgressTracker(() => _now));
        var stages = seen.Select(snapshot => LauncherWizardWindow.CreateStageIndex(snapshot.Phase)).ToList();

        stages.Should().BeInAscendingOrder();
        stages.Distinct().Should().Equal(0, 1, 2, 3);
        seen[stages.IndexOf(2)].Phase.Should().Be(CreateProgressTracker.Phase.FinalCheck);
        seen.Count(snapshot => LauncherWizardWindow.CreateStageIndex(snapshot.Phase) == 2)
            .Should().BeGreaterThan(seen.Count / 5, "checking takes about a third of the run and is seen as such");
    }

    [Fact]
    public void ARepeatedStageOrAWaitForTheDeviceNeverStepsTheRunBack()
    {
        var tracker = new CreateProgressTracker(() => _now);
        Report(tracker, UsbProvisioningStage.WritingSystemImage, Image / 2, Image);
        var before = Report(tracker, UsbProvisioningStage.WritingSystemImage, Image / 2, Image);

        var retried = Report(tracker, UsbProvisioningStage.CleaningDisk);
        var waiting = Report(tracker, UsbProvisioningStage.WaitingForDevice);

        retried.Phase.Should().Be(CreateProgressTracker.Phase.WritingBootImage);
        waiting.Phase.Should().Be(CreateProgressTracker.Phase.WritingBootImage);
        retried.Overall.Should().BeGreaterThanOrEqualTo(before.Overall);
    }

    [Fact]
    public void AStageWithoutBytesCreepsSoTheBarIsSeenToBeAlive()
    {
        var tracker = new CreateProgressTracker(() => _now);
        var start = Report(tracker, UsbProvisioningStage.CleaningDisk);
        _now += TimeSpan.FromSeconds(20);
        var later = Report(tracker, UsbProvisioningStage.CleaningDisk);

        later.Overall.Should().BeGreaterThan(start.Overall);
        later.PhaseElapsed.Should().Be(TimeSpan.FromSeconds(20));
        later.Speed.Should().Be(CreateProgressTracker.SpeedKind.None);
    }

    [Fact]
    public void SpeedSaysWhetherItIsWritingOrReading()
    {
        var tracker = new CreateProgressTracker(() => _now);
        var validating = Report(tracker, UsbProvisioningStage.Validating, Image / 2, Image);
        validating.Speed.Should().Be(CreateProgressTracker.SpeedKind.Read);
        validating.Remaining.Should().BeNull("nothing has been written to time yet");

        CreateProgressTracker.Snapshot writing = null!;
        for (var second = 0; second <= 20; second++)
        {
            _now += TimeSpan.FromSeconds(1);
            writing = Report(tracker, UsbProvisioningStage.WritingSystemImage, second * 40L * 1024 * 1024, Image);
        }

        writing.Speed.Should().Be(CreateProgressTracker.SpeedKind.Write);
        writing.BytesPerSecond.Should().BeApproximately(40d * 1024 * 1024, 1024 * 1024);
        writing.Remaining!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(1));

        _now += TimeSpan.FromSeconds(1);
        var check = Report(tracker, UsbProvisioningStage.Verifying, 1, 2 * Image);
        check.Speed.Should().Be(CreateProgressTracker.SpeedKind.Read);
        check.Remaining.Should().NotBeNull();
    }

    [Fact]
    public void TimeLeftCountsDownWhileTheSpeedHoldsAndDoesNotJumpWithABurstOrAStall()
    {
        var tracker = new CreateProgressTracker(() => _now);
        const long mb = 1024 * 1024;
        var shown = new List<double>();
        long done = 0;
        for (var second = 1; second <= 60; second++)
        {
            _now += TimeSpan.FromSeconds(1);
            // 20 MB/s, with a two-second burst and a two-second stall on the way.
            done += second is 30 or 31 ? 80 * mb : second is 40 or 41 ? 0 : 20 * mb;
            var snapshot = Report(tracker, UsbProvisioningStage.WritingSystemImage, done, 2 * Image);
            if (second > 25 && snapshot.Remaining is { } left) shown.Add(left.TotalSeconds);
        }

        shown.Should().NotBeEmpty();
        shown.Zip(shown.Skip(1), (before, after) => after - before)
            .Should().OnlyContain(change => change < 5, "the time left never jumps up by more than a few seconds");
        (shown[0] - shown[^1]).Should().BeGreaterThan(15, "over half a minute of steady writing it comes down");
    }

    [Fact]
    public void NoTimeLeftIsGuessedFromTheSticksOwnCacheBurstAtTheStart()
    {
        // 78 MB/s for the first seconds, then the stick's real 20 MB/s: an estimate from the
        // burst rose from 2:04 to 2:47 on screen.
        var tracker = new CreateProgressTracker(() => _now);
        const long mb = 1024 * 1024;
        long done = 0;
        var shown = new List<TimeSpan?>();
        for (var second = 1; second <= 40; second++)
        {
            _now += TimeSpan.FromSeconds(1);
            done += second <= 6 ? 78 * mb : 20 * mb;
            shown.Add(Report(tracker, UsbProvisioningStage.WritingSystemImage, done, Image).Remaining);
        }

        shown.Take(10).Should().OnlyContain(remaining => remaining == null);
        shown.Skip(12).Should().OnlyContain(remaining => remaining != null);
    }
}
