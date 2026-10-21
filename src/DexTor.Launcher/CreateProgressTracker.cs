using DexTor.Contracts;
using DexTor.Creator;

namespace DexTor.Launcher;

/// <summary>
/// Turns the provisioner's stage-by-stage reports into what the creation page shows: one
/// percentage for the whole run, the phase in plain words, one byte counter per line of the
/// stage list, a speed that says whether it is writing or reading, and a time left.
/// </summary>
/// <remarks>
/// <para>
/// Testers saw the percentage restart with every stage (0→76%, 3→39%), the step name go
/// round in a circle, "Processed" drop back to 0 within "Writing the image" and its total
/// change from 3.5 to 1.7 GB, and "Time left" go blank for half a minute. The provisioner now
/// writes everything first (the boot image, then the copy for Windows) and checks everything
/// after, reading from the stick; so "Writing the image" counts the two writes on one counter
/// and "Checking the write" the two reads on another.
/// </para>
/// <para>
/// Every phase has a fixed place and a share of the run in proportion to how long it takes,
/// the run only moves forward, and the time left is built from write and read speeds averaged
/// over twenty seconds. It is shown from the first measured write to the end, phases without
/// bytes included, so it never goes blank.
/// </para>
/// </remarks>
internal sealed class CreateProgressTracker
{
    internal enum Phase
    {
        Preparing,
        CheckingImage,
        ErasingUsb,
        WritingBootImage,
        CreatingDataVolume,
        CreatingStorage,
        CopyingWindowsImage,
        FinalCheck,
        Complete
    }

    internal enum SpeedKind
    {
        None,
        Write,
        Read
    }

    /// <param name="StageBytesDone">The counter of the stage-list line under way: both writes, or both reads.</param>
    internal sealed record Snapshot(
        Phase Phase,
        double Overall,
        SpeedKind Speed,
        double? BytesPerSecond,
        TimeSpan? Remaining,
        long PhaseBytesDone,
        long PhaseBytesTotal,
        TimeSpan PhaseElapsed,
        long StageBytesDone = 0,
        long StageBytesTotal = 0);

    // Shares of the whole run, in proportion to the seconds each phase takes for a 1.7 GB
    // image on a stick writing 20 MB/s and reading 40 MB/s: the boot image written (~85 s),
    // the copy for Windows written (~85 s), both read back from the stick (~85 s); erasing,
    // formatting and the sparse storage take seconds.
    private static readonly double[] Weights = [1, 2, 3, 26, 3, 1, 26, 26, 0];

    /// <summary>A phase that reports no bytes creeps towards, never reaches, its end.</summary>
    private static readonly TimeSpan CreepTimeConstant = TimeSpan.FromSeconds(15);

    /// <summary>What finishing touches cost after the last byte, for the estimate.</summary>
    private static readonly TimeSpan Tail = TimeSpan.FromSeconds(10);

    /// <summary>The window speeds are averaged over: long enough to ride out a stick's own bursts.</summary>
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long writing is timed before a time left is shown: a stick takes its first
    /// hundreds of megabytes into its own cache (78 MB/s on a 20 MB/s stick), and an estimate
    /// from those seconds rose from 2:04 to 2:47 as the real speed showed.
    /// </summary>
    internal static readonly TimeSpan EstimateWarmUp = TimeSpan.FromSeconds(12);

    /// <summary>How quickly the time shown follows an estimate that came down.</summary>
    private static readonly TimeSpan RemainingFallTimeConstant = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How slowly it follows one that went up: a time left that grows while the operator
    /// watches ("14:43, then 20:14") reads as the run going wrong.
    /// </summary>
    private static readonly TimeSpan RemainingRiseTimeConstant = TimeSpan.FromSeconds(40);

    /// <summary>
    /// What creating data.img writes when no figure has come: nothing, it is a sparse file. Only
    /// a volume that cannot make it sparse writes it whole, and then reports the bytes.
    /// </summary>
    internal const long DefaultStorageWriteBytes = 0;

    private readonly Func<TimeSpan> _clock;
    private Phase _phase = Phase.Preparing;
    private TimeSpan _phaseStarted;
    private double _overall;
    private SpeedKind _sampleKind;
    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new();
    private double? _writeSpeed;
    private double? _readSpeed;
    private double? _shownRemainingSeconds;
    private TimeSpan _shownAt;
    private long _imageBytes;
    private long _storageWriteBytes = DefaultStorageWriteBytes;
    private long _storageWritten;
    private TimeSpan? _firstWriteAt;

    public CreateProgressTracker(Func<TimeSpan> clock)
    {
        _clock = clock;
        _phaseStarted = clock();
    }

    public Snapshot Observe(CreatorActionService.CreatorStepProgress progress)
    {
        var now = _clock();
        var reported = PhaseOf(progress);
        // A stage repeated or a wait for the device keeps the phase where it was: the
        // operator never sees the run step backwards.
        if (reported > _phase)
        {
            _phase = reported;
            _phaseStarted = now;
        }

        var bytesDone = reported == _phase ? progress.BytesDone : 0;
        var bytesTotal = reported == _phase ? progress.BytesTotal : 0;
        if (_phase is Phase.WritingBootImage or Phase.CopyingWindowsImage && bytesTotal > 0)
        {
            _imageBytes = bytesTotal;
        }

        if (_phase == Phase.FinalCheck && bytesTotal > 0 && _imageBytes <= 0)
        {
            _imageBytes = bytesTotal / 2;
        }

        if (_phase == Phase.CreatingStorage && bytesTotal > 0)
        {
            _storageWriteBytes = bytesTotal;
            _storageWritten = bytesDone;
        }

        var within = WithinPhase(bytesDone, bytesTotal, now - _phaseStarted);
        var overall = _phase == Phase.Complete ? 1 : (Start(_phase) + Weights[(int)_phase] * within) / Weights.Sum();
        _overall = Math.Max(_overall, Math.Min(overall, 1));

        var kind = SpeedKindOf(_phase, bytesTotal);
        var speed = kind == SpeedKind.None ? null : Measure(kind, now, bytesDone);
        if (kind == SpeedKind.Write && _firstWriteAt is null) _firstWriteAt = now;
        TimeSpan? remaining = null;
        if (_phase != Phase.Complete && now - (_firstWriteAt ?? now) >= EstimateWarmUp && EstimateRemaining(bytesDone, bytesTotal) is { } estimate)
        {
            remaining = TimeSpan.FromSeconds(Math.Max(1, Smooth(now, estimate)));
        }

        var (stageDone, stageTotal) = StageCounter(bytesDone, bytesTotal);
        return new Snapshot(_phase, _overall, kind, speed, remaining, bytesDone, bytesTotal, now - _phaseStarted, stageDone, stageTotal);
    }

    internal static Phase PhaseOf(CreatorActionService.CreatorStepProgress progress)
    {
        foreach (var (stage, phase) in StagePhases)
        {
            if (progress.MessageKey == UsbProvisionerClient.StageMessageKey(stage)) return phase;
        }

        return progress.Step switch
        {
            CreatorStep.PrepareRelease or CreatorStep.ConfirmErase => Phase.Preparing,
            CreatorStep.FormatUsb => Phase.ErasingUsb,
            CreatorStep.WriteSystemImage => Phase.WritingBootImage,
            CreatorStep.CreateDataImage => Phase.CreatingStorage,
            CreatorStep.VerifyUsb or CreatorStep.ProvisionGuest => Phase.FinalCheck,
            _ => Phase.Complete
        };
    }

    private static readonly (UsbProvisioningStage Stage, Phase Phase)[] StagePhases =
    [
        (UsbProvisioningStage.Validating, Phase.CheckingImage),
        (UsbProvisioningStage.CleaningDisk, Phase.ErasingUsb),
        (UsbProvisioningStage.WritingSystemImage, Phase.WritingBootImage),
        (UsbProvisioningStage.FormattingDataVolume, Phase.CreatingDataVolume),
        (UsbProvisioningStage.WaitingForDataVolume, Phase.CreatingDataVolume),
        (UsbProvisioningStage.CreatingPrivateStorage, Phase.CreatingStorage),
        (UsbProvisioningStage.CopyingWindowsImage, Phase.CopyingWindowsImage),
        (UsbProvisioningStage.Verifying, Phase.FinalCheck),
        (UsbProvisioningStage.Complete, Phase.Complete)
    ];

    private static double Start(Phase phase) => Weights.Take((int)phase).Sum();

    private static double WithinPhase(long done, long total, TimeSpan elapsed) => total <= 0
        ? 0.95 * (1 - Math.Exp(-elapsed.TotalSeconds / CreepTimeConstant.TotalSeconds))
        : Math.Clamp(done / (double)total, 0, 1);

    private static SpeedKind SpeedKindOf(Phase phase, long total)
    {
        if (total <= 0) return SpeedKind.None;
        return phase is Phase.WritingBootImage or Phase.CreatingStorage or Phase.CopyingWindowsImage
            ? SpeedKind.Write
            : SpeedKind.Read;
    }

    /// <summary>
    /// "Writing the image" counts the boot image and the copy for Windows on one counter of
    /// twice the image size; "Checking the write" has the provisioner's own counter of both reads.
    /// </summary>
    private (long Done, long Total) StageCounter(long done, long total)
    {
        if (_imageBytes <= 0) return (done, total);
        var writes = 2 * _imageBytes;
        return _phase switch
        {
            Phase.WritingBootImage => (done, writes),
            Phase.CreatingDataVolume or Phase.CreatingStorage => (_imageBytes, writes),
            Phase.CopyingWindowsImage => (_imageBytes + done, writes),
            _ => (done, total)
        };
    }

    /// <summary>Bytes per second over the last <see cref="SpeedWindow"/> of the current kind of transfer.</summary>
    private double? Measure(SpeedKind kind, TimeSpan now, long bytes)
    {
        if (kind != _sampleKind || (_samples.Count > 0 && bytes < _samples.Last().Bytes))
        {
            _sampleKind = kind;
            _samples.Clear();
        }

        _samples.Enqueue((now, bytes));
        while (_samples.Count > 2 && now - _samples.Peek().At > SpeedWindow)
        {
            _samples.Dequeue();
        }

        var (firstAt, firstBytes) = _samples.Peek();
        var window = (now - firstAt).TotalSeconds;
        if (window < 1) return kind == SpeedKind.Write ? _writeSpeed : _readSpeed;
        var measured = (bytes - firstBytes) / window;
        if (kind == SpeedKind.Write)
        {
            _writeSpeed = measured;
            return _writeSpeed;
        }

        // Reads of the cached image on the computer's own disk say nothing about the stick.
        if (_phase != Phase.CheckingImage)
        {
            _readSpeed = measured;
        }

        return _readSpeed ?? measured;
    }

    /// <summary>
    /// The time shown counts down by itself and follows a new estimate over seconds, so a
    /// burst or a stall of a second does not swing it by minutes.
    /// </summary>
    private double Smooth(TimeSpan now, double estimate)
    {
        if (_shownRemainingSeconds is not { } shown)
        {
            _shownRemainingSeconds = estimate;
        }
        else
        {
            var elapsed = Math.Max(0, (now - _shownAt).TotalSeconds);
            shown = Math.Max(0, shown - elapsed);
            var constant = estimate > shown ? RemainingRiseTimeConstant : RemainingFallTimeConstant;
            var follow = 1 - Math.Exp(-elapsed / constant.TotalSeconds);
            _shownRemainingSeconds = shown + (estimate - shown) * follow;
        }

        _shownAt = now;
        return _shownRemainingSeconds.Value;
    }

    /// <summary>Seconds to the end of the run, from the work still to do and the speeds seen.</summary>
    private double? EstimateRemaining(long done, long total)
    {
        if (_writeSpeed is not > 0 || _imageBytes <= 0) return null;
        var write = _writeSpeed.Value;
        // Until a read-back has been timed, assume a stick reads twice as fast as it writes.
        var read = _readSpeed is > 0 ? _readSpeed.Value : write * 2;
        var storageLeft = Math.Max(0, _storageWriteBytes - _storageWritten);
        double writeLeft;
        double readLeft = 2 * _imageBytes;
        switch (_phase)
        {
            case Phase.WritingBootImage:
                writeLeft = Math.Max(0, _imageBytes - done) + storageLeft + _imageBytes;
                break;
            case Phase.CreatingDataVolume:
                writeLeft = storageLeft + _imageBytes;
                break;
            case Phase.CreatingStorage:
                writeLeft = (total > 0 ? Math.Max(0, total - done) : storageLeft) + _imageBytes;
                break;
            case Phase.CopyingWindowsImage:
                writeLeft = Math.Max(0, _imageBytes - done);
                break;
            case Phase.FinalCheck:
                writeLeft = 0;
                readLeft = total > 0 ? Math.Max(0, total - done) : readLeft;
                break;
            default:
                return null;
        }

        return writeLeft / write + readLeft / read + Tail.TotalSeconds;
    }
}
