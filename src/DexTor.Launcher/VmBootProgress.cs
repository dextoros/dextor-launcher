using System.Globalization;
using DexTor.Contracts;
using DexTor.SessionHost;

namespace DexTor.Launcher;

/// <summary>
/// What the launch page shows while DexTor OS boots with its window still hidden: which of
/// four steps it is on and a bar that moves with the time a boot takes on this computer.
/// </summary>
/// <remarks>
/// The window opens only once DexTor OS has drawn its splash (see GuestDisplay), which takes
/// about a minute. A black window for that long looked like a hang; a spinning bar and one
/// line did too. The steps come from what the guest's hidden screen shows, and the bar from
/// the last boot, remembered in <c>%LOCALAPPDATA%\DexTor\vm-boot-seconds.txt</c>. A bar
/// stays inside the share of the step it is on, so it never runs ahead of the guest, and it
/// only moves forward.
/// </remarks>
internal sealed class VmBootProgress
{
    /// <summary>The four lines on the launch page.</summary>
    internal static readonly string[] StageKeys =
    [
        "launcher.wizard.vm.stage_prepare",
        "launcher.wizard.vm.stage_machine",
        "launcher.wizard.vm.stage_system",
        "launcher.wizard.vm.stage_window"
    ];

    /// <summary>What a first boot is assumed to take, until one has been timed.</summary>
    internal static readonly TimeSpan DefaultBootTime = TimeSpan.FromSeconds(75);

    // Where each step's share of the bar starts and ends.
    private static readonly (double From, double To)[] Shares = [(0, 0.08), (0.08, 0.3), (0.3, 0.97), (0.97, 1)];

    private readonly TimeSpan _expected;
    private double _shown;

    internal VmBootProgress(TimeSpan? lastBoot)
    {
        LastBoot = lastBoot;
        _expected = lastBoot is { } known && known > TimeSpan.FromSeconds(5) ? known : DefaultBootTime;
    }

    /// <summary>How long the last boot took on this computer; null before the first.</summary>
    internal TimeSpan? LastBoot { get; }

    internal int Stage { get; private set; }

    internal static int StageOf(SessionStartStage stage) => stage switch
    {
        SessionStartStage.CheckingUsb or SessionStartStage.CheckingRuntime or SessionStartStage.LockingStorage => 0,
        SessionStartStage.StartingQemu or SessionStartStage.ConnectingControlChannel or SessionStartStage.GuestFirmware => 1,
        SessionStartStage.GuestLoadingSystem => 2,
        _ => 3
    };

    /// <summary>Moves to the step of <paramref name="stage"/>; never back.</summary>
    internal void Observe(SessionStartStage stage)
    {
        // "Slow" is a remark about the step under way, not a step.
        if (stage == SessionStartStage.GuestSlow) return;
        Stage = Math.Max(Stage, StageOf(stage));
    }

    /// <summary>The bar, 0 to 1, after <paramref name="elapsed"/> of the start.</summary>
    internal double Fraction(TimeSpan elapsed)
    {
        var (from, to) = Shares[Stage];
        var byTime = elapsed.TotalSeconds / _expected.TotalSeconds;
        // Past the expected time it creeps towards the end of the step, never reaching it.
        if (byTime > to) byTime = to - (to - from) * 0.1 * Math.Exp(-(byTime - to) * 4);
        var value = Math.Clamp(byTime, from, Stage == Shares.Length - 1 ? to : to - 0.005);
        _shown = Math.Max(_shown, value);
        return _shown;
    }

    private static string StorePath => Path.Combine(DexTorStorage.Root, "vm-boot-seconds.txt");

    internal static TimeSpan? ReadLastBoot()
    {
        try
        {
            return File.Exists(StorePath)
                && double.TryParse(File.ReadAllText(StorePath).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
                && seconds is > 0 and < 3600
                    ? TimeSpan.FromSeconds(seconds)
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static void RememberBoot(TimeSpan took)
    {
        try
        {
            Directory.CreateDirectory(DexTorStorage.Root);
            File.WriteAllText(StorePath, took.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the next estimate is less good.
        }
    }
}
