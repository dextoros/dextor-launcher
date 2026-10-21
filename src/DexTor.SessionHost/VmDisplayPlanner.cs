using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DexTor.SessionHost;

/// <summary>
/// The screen DexTor OS is shown on: the monitor the launcher is on, its work area and Windows
/// scale, all in physical pixels, and the picture area the VM window has there when maximized.
/// </summary>
/// <param name="Width">The picture area of the maximized VM window: the work area less the title bar.</param>
/// <param name="WorkLeft">The monitor's work area (without the taskbar), physical pixels.</param>
public sealed record VmDisplay(
    int Width,
    int Height,
    int ScalePercent,
    int WorkLeft,
    int WorkTop,
    int WorkRight,
    int WorkBottom);

/// <summary>Works out <see cref="VmDisplay"/> for the monitor a window is on.</summary>
/// <remarks>
/// <para>
/// The guest used to start at QEMU's 1280x800 and the window stretched it 1.65 times on a
/// 2560x1440 screen: a soft logo and stair-stepped letters, then a mode change that left the
/// splash in a corner. It now starts in the size of the maximized window's picture area, so
/// one guest pixel is one screen pixel and no mode change is needed.
/// </para>
/// <para>
/// The launcher is system-DPI aware, which makes Windows report other monitors in scaled
/// figures; the measurement runs with the thread switched to per-monitor awareness, and QEMU's
/// SDL window is made per-monitor aware too (SDL_WINDOWS_DPI_AWARENESS), so both count the same
/// physical pixels. The guest learns the Windows scale and draws its desktop at that scale -
/// rather than drawing small and having the window stretch it, which a 150% scale could only
/// do blurred.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class VmDisplayPlanner
{
    /// <summary>The display of the monitor <paramref name="anchor"/> is on; null when it cannot be read.</summary>
    public static VmDisplay? ForWindow(IntPtr anchor)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var monitor = MonitorFromWindow(anchor, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return null;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info)) return null;
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpi, out _) != 0 || dpi == 0) dpi = 96;
            // What a maximized overlapped window - SDL's - keeps of its frame at the top: the
            // title bar. The frame itself lies outside the work area.
            var frame = new NativeRect();
            var caption = AdjustWindowRectExForDpi(ref frame, WsOverlappedWindow, false, 0, dpi)
                ? -frame.Top - (-frame.Left)
                : GetSystemMetricsForDpi(SmCyCaption, dpi);
            return Plan(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom, (int)dpi, caption);
        }
        finally
        {
            if (previous != IntPtr.Zero) _ = SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>The figures for a work area, a monitor DPI and that DPI's title bar height.</summary>
    internal static VmDisplay Plan(int left, int top, int right, int bottom, int dpi, int captionHeight)
    {
        // A maximized window's frame lies outside the work area; its title bar is inside.
        var width = Math.Max(640, right - left);
        var height = Math.Max(480, bottom - top - Math.Max(0, captionHeight));
        var scale = Math.Clamp((int)Math.Round(dpi * 100.0 / 96 / 25) * 25, 100, 400);
        // Not rounded to Steps: a maximized window cannot be made smaller (Windows ignores it,
        // measured), and QEMU tells the guest the window's own size as soon as it is shown -
        // a rounded first mode would only be followed by a mode change. DexTor OS fits it.
        return new VmDisplay(width, height, scale, left, top, right, bottom);
    }

    /// <summary>
    /// What the guest's width and height must be multiples of (DexTor OS, 2026-10-19): the width
    /// of 16, or virtio-gpu skews every frame diagonally (seen at 2559); and both sides of the
    /// scale's numerator, or GNOME cannot apply the scale - at 150% (3/2) a width of 48 and a
    /// height of 3, at 125% (5/4) 80 and 5, at 175% (7/4) 112 and 7, at 200% (2/1) 16 and 2.
    /// </summary>
    internal static (int Width, int Height) Steps(int scalePercent)
    {
        var divisor = Gcd(scalePercent, 100);
        var numerator = scalePercent / divisor;
        return (Lcm(16, numerator), numerator);
    }

    private static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    private static int Lcm(int a, int b) => a / Gcd(a, b) * b;

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;
    private const int SmCyCaption = 4;
    private const uint WsOverlappedWindow = 0x00CF0000;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustWindowRectExForDpi(ref NativeRect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint exStyle, uint dpi);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetricsForDpi(int index, uint dpi);
}
