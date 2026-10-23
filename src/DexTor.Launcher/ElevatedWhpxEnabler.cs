using System.ComponentModel;
using System.Diagnostics;

namespace DexTor.Launcher;

/// <summary>
/// Turns on Windows Hypervisor Platform from a launcher that is not elevated, by starting
/// itself elevated for just that step.
/// </summary>
/// <remarks>
/// The portable launcher asks the person to start it as administrator instead. A launcher
/// from the Store package cannot be started that way: Windows offers "Run as
/// administrator" for a packaged app only when its package declares the restricted
/// allowElevation capability. So the packaged launcher restarts its own executable
/// through the UAC prompt - the same way USB writing does (UsbProvisionerClient) - and the
/// elevated copy runs DISM and bcdedit and exits with 0 on success.
/// </remarks>
internal static class ElevatedWhpxEnabler
{
    /// <summary>The first argument that makes the launcher only enable the feature and exit.</summary>
    internal const string ModeArgument = "--enable-whpx";

    internal static ProcessStartInfo BuildStartInfo(string launcherExecutable) => new(launcherExecutable)
    {
        Arguments = ModeArgument,
        UseShellExecute = true,
        Verb = "runas",
        WindowStyle = ProcessWindowStyle.Hidden
    };

    /// <summary>
    /// Starts the elevated copy and waits for it. False when the person declines the UAC
    /// prompt, the copy cannot start, or enabling the feature fails.
    /// </summary>
    internal static bool TryRunElevated()
    {
        var launcher = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(launcher) || !File.Exists(launcher))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(BuildStartInfo(launcher));
            if (process is null)
            {
                return false;
            }

            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            // 1223 (ERROR_CANCELLED) when the UAC prompt is declined; the caller then shows
            // how to turn the feature on by hand.
            return false;
        }
    }
}
