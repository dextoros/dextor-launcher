using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DexTor.Launcher.Tests;

internal static partial class TestAssemblySetup
{
    /// <summary>
    /// The fake QEMU never opens a window, so a start must not wait 15 seconds for one; and
    /// nothing a test does may land in the user's real %LOCALAPPDATA%\DexTor - a test run
    /// once left a vm-profile.json there that described the fake QEMU.
    /// </summary>
    [ModuleInitializer]
    internal static void Isolate()
    {
        // Negative executable tests must return loader errors instead of waiting for
        // Windows' "Unsupported 16-Bit Application" dialog. Child processes inherit this
        // mode; keep the existing flags and change only this test host's process state.
        if (OperatingSystem.IsWindows()) _ = SetErrorMode(GetErrorMode() | 0x0001);
        DexTor.SessionHost.QemuWindowManager.WindowWaitTimeout = TimeSpan.Zero;
        DexTor.Contracts.DexTorStorage.RootOverride ??= Path.Combine(
            Path.GetTempPath(), "dextor-tests-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetErrorMode();

    [LibraryImport("kernel32.dll")]
    private static partial uint SetErrorMode(uint mode);
}
