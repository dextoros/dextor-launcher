using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DexTor.SessionHost;

/// <summary>
/// Best-effort placement of the QEMU window. Never fails a VM start.
/// </summary>
/// <remarks>
/// The window is found by enumerating top-level windows and matching the owning process
/// id. That is only safe because the session host holds QEMU's original process handle
/// for the whole session, which stops Windows reusing the id — without it, a reused id
/// could put another application's window in front of the user.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static partial class QemuWindowManager
{
    private const int ShowMaximized = 3;

    /// <summary>How long a start waits for the window; the test assembly sets it to zero.</summary>
    internal static TimeSpan WindowWaitTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>How long the window stays hidden at most while the guest has drawn nothing.</summary>
    internal static readonly TimeSpan DisplayWaitTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// After this long without a picture the launcher says the start is slow and offers to wait
    /// on or to look closer; the window is still not shown on its own.
    /// </summary>
    internal static readonly TimeSpan SlowAfter = TimeSpan.FromMinutes(3);

    /// <summary>The longest the guard waits for "desktop" - a storage screen left open for good ends it.</summary>
    private static readonly TimeSpan DesktopWaitLimit = TimeSpan.FromMinutes(30);

    /// <summary>The most a reveal waits for the maximized window's first frame before the curtain goes.</summary>
    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(2);

    private const int Hide = 0;

    /// <summary>
    /// Finds QEMU's window - started hidden - and keeps it hidden until DexTor OS has a picture,
    /// then shows it maximized on the launcher's monitor, behind the boot curtain, and lifts the
    /// curtain once the window shows its first frame. Until the desktop is up, the curtain comes
    /// back whenever the guest's screen goes to QEMU's placeholder or to black.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Display output is not active." must never be seen, on any computer at any speed: the
    /// window is shown only on a signal - DexTor OS's "frame" over virtio-serial, or a
    /// screendump with a picture - never after a fixed wait. A start that takes longer than
    /// <see cref="SlowAfter"/> is reported as slow; the window then still waits for a picture,
    /// or for the operator to ask for it.
    /// </para>
    /// <para>
    /// Showing a hidden SDL window let a frame of its old size and Windows' opening animation
    /// through - testers saw a translucent title bar and a picture shifted right for a quarter
    /// of a second. Both now happen under the curtain, a launcher window that covers the
    /// monitor's work area.
    /// </para>
    /// </remarks>
    /// <returns>False only when the window was there and the guest drew nothing in time.</returns>
    public static async Task<bool> RevealWhenDrawingAsync(
        IQemuProcess process,
        Func<CancellationToken, Task<GuestScreen>> guestScreen,
        CancellationToken cancellationToken,
        VmWindowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(guestScreen);
        options ??= VmWindowOptions.None;
        var window = IntPtr.Zero;
        var watcher = WindowWaitTimeout > TimeSpan.Zero ? QemuWindowWatcher.Start(process.Id, QemuWindowWatcher.Mode.Hidden) : null;
        var ready = false;
        try
        {
            var deadline = DateTime.UtcNow + WindowWaitTimeout;
            while (window == IntPtr.Zero && DateTime.UtcNow < deadline && !process.HasExited)
            {
                // QEMU starts with its window hidden (see QemuProcessLauncher), so it is found
                // by the hook or, failing that, as the first SDL window QEMU made.
                window = watcher?.MainWindow is { } seen && seen != IntPtr.Zero ? seen : FindMainWindow(process.Id);
                if (window == IntPtr.Zero) window = FindFirstSdlWindow(process.Id);
                if (window == IntPtr.Zero) await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }

            if (window == IntPtr.Zero)
            {
                watcher?.Dispose();
                return true;
            }

            _ = ShowWindowAsync(window, Hide);
            var drew = false;
            // Alive means anything but the placeholder; ready means a picture in a graphics
            // mode - not the firmware's or GRUB's text, not the black between them and the splash.
            var aliveBy = DateTime.UtcNow + DisplayWaitTimeout;
            var slowAt = DateTime.UtcNow + SlowAfter;
            while (!process.HasExited)
            {
                if (options.Signals?.Invoke() is { FrameReady: true } || options.ShowNow?.Invoke() == true)
                {
                    drew = ready = true;
                    break;
                }

                var screen = await guestScreen(cancellationToken).ConfigureAwait(false);
                if (GuestDisplay.IsAlive(screen)) drew = true;
                if (screen == GuestScreen.Picture)
                {
                    ready = true;
                    break;
                }

                // A guest that has drawn nothing by now is stuck in its firmware; QEMU itself
                // can go on running for ever.
                if (!drew && DateTime.UtcNow >= aliveBy) break;
                if (DateTime.UtcNow >= slowAt)
                {
                    options.Slow?.Invoke();
                    slowAt = DateTime.UtcNow + SlowAfter;
                }

                // The guest's "frame" ends the wait at once; the look at the screen is the fallback.
                var pause = Task.Delay(drew ? 400 : 250, cancellationToken);
                if (options.Signals?.Invoke() is { } frameSignals) await Task.WhenAny(pause, frameSignals.Frame).ConfigureAwait(false);
                else await pause.ConfigureAwait(false);
            }

            // Gone QEMU is a different failure, reported as such.
            if (process.HasExited) return true;
            if (!ready)
            {
                watcher?.Dispose();
                return drew;
            }

            await ShowBehindCurtainAsync(process, window, watcher, guestScreen, options, cancellationToken).ConfigureAwait(false);
            _ = GuardBootAsync(process, window, watcher, guestScreen, options);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Window placement must never fail VM startup; a window that was ready is shown.
            if (ready && window != IntPtr.Zero)
            {
                MarkRevealed(process.Id);
                _ = ShowWindowAsync(window, ShowMaximized);
                options.Curtain?.Uncover();
            }

            return true;
        }
    }

    /// <summary>
    /// The curtain first, then the window moved to the launcher's monitor and maximized under
    /// it; the curtain lifts once the window is maximized and the guest has drawn into it.
    /// </summary>
    private static async Task ShowBehindCurtainAsync(
        IQemuProcess process,
        IntPtr window,
        QemuWindowWatcher? watcher,
        Func<CancellationToken, Task<GuestScreen>> guestScreen,
        VmWindowOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Display is { } display)
        {
            options.Curtain?.Cover(display);
            MoveToMonitor(window, display);
        }

        // The picture area is kept to sizes DexTor OS can use without skew and at its scale.
        if (watcher is not null && options.Display is { } screen) watcher.SnapSteps = VmDisplayPlanner.Steps(screen.ScalePercent);
        DisableTransitions(window);
        watcher?.Current = QemuWindowWatcher.Mode.Maximized;
        MarkRevealed(process.Id);
        _ = ShowWindowAsync(window, ShowMaximized);
        var settleBy = DateTime.UtcNow + SettleLimit;
        while (DateTime.UtcNow < settleBy && !process.HasExited && !(IsWindowVisible(window) && IsZoomed(window)))
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        // One more picture after the window took its size: that frame was drawn into it.
        while (DateTime.UtcNow < settleBy && !process.HasExited
               && await guestScreen(cancellationToken).ConfigureAwait(false) != GuestScreen.Picture)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        _ = SetForegroundWindow(window);
        options.Curtain?.Uncover();
    }

    /// <summary>
    /// Until the desktop is up: the curtain covers the window whenever the guest's screen is
    /// QEMU's placeholder or black - a mode change, the gap between the splash and the desktop.
    /// The placeholder is caught the moment SDL resizes the window for it; screendumps catch
    /// black and tell when the picture is back.
    /// </summary>
    private static async Task GuardBootAsync(
        IQemuProcess process,
        IntPtr window,
        QemuWindowWatcher? watcher,
        Func<CancellationToken, Task<GuestScreen>> guestScreen,
        VmWindowOptions options)
    {
        var covered = false;
        if (watcher is not null && options.Curtain is { } curtain && options.Display is { } display)
        {
            watcher.PlaceholderShown = () =>
            {
                covered = true;
                curtain.Cover(display);
            };
        }

        try
        {
            // With DexTor OS's signals the guard lasts until "desktop" - the storage screen in
            // between may wait for the operator's password as long as it takes; without them, the
            // boot phase. Dark frames between the splash, the storage screen and the desktop
            // (Cage starting, mutter taking the display, GNOME Shell's #282828) are covered.
            var started = DateTime.UtcNow;
            bool Booting() => options.Signals?.Invoke() is { } guest
                ? !guest.DesktopReady && DateTime.UtcNow - started < DesktopWaitLimit
                : DateTime.UtcNow - started < BootPhase;
            while (!process.HasExited && Booting())
            {
                var screen = await guestScreen(CancellationToken.None).ConfigureAwait(false);
                if (options.Curtain is { } shade && options.Display is { } area)
                {
                    if (screen is GuestScreen.Placeholder or GuestScreen.Blank && !covered)
                    {
                        covered = true;
                        shade.Cover(area);
                    }
                    else if (screen == GuestScreen.Picture && covered)
                    {
                        covered = false;
                        _ = SetForegroundWindow(window);
                        shade.Uncover();
                    }
                }

                // Close looks while the boot moves; easier ones while the storage screen waits.
                await Task.Delay(DateTime.UtcNow - started < BootPhase ? 150 : 400).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or TimeoutException)
        {
            // The guard is cosmetic; the session goes on.
        }
        finally
        {
            if (watcher is not null) watcher.PlaceholderShown = null;
            options.Curtain?.Uncover();
            // The window stays fitted for the whole session; only maximizing stops here.
            watcher?.Current = QemuWindowWatcher.Mode.Off;
            if (watcher is not null) _ = DisposeWhenGoneAsync(watcher, process);
            // Past the boot phase nothing asks any more; the id may be reused by another process.
            lock (s_revealed) s_revealed.Remove(process.Id);
        }
    }

    private static async Task DisposeWhenGoneAsync(QemuWindowWatcher watcher, IQemuProcess process)
    {
        while (!process.HasExited) await Task.Delay(1000).ConfigureAwait(false);
        watcher.Dispose();
    }

    /// <summary>Puts the hidden window on the launcher's monitor, so maximizing fills that one.</summary>
    private static void MoveToMonitor(IntPtr window, VmDisplay display)
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            _ = SetWindowPos(window, IntPtr.Zero, display.WorkLeft + 32, display.WorkTop + 32, 0, 0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            if (previous != IntPtr.Zero) _ = SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>No opening animation for QEMU's window; ignored where Windows does not allow it.</summary>
    private static void DisableTransitions(IntPtr window)
    {
        var disabled = 1;
        _ = DwmSetWindowAttribute(window, DwmwaTransitionsForceDisabled, ref disabled, sizeof(int));
    }

    private const int DwmwaTransitionsForceDisabled = 3;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private static readonly HashSet<int> s_revealed = [];

    /// <summary>The window of <paramref name="processId"/> has been shown on purpose; until then it stays hidden.</summary>
    private static void MarkRevealed(int processId)
    {
        lock (s_revealed) s_revealed.Add(processId);
    }

    private static bool IsRevealed(int processId)
    {
        lock (s_revealed) return s_revealed.Contains(processId);
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    /// <summary>
    /// How long after start the window is kept maximized while the guest changes video modes.
    /// </summary>
    public static readonly TimeSpan BootPhase = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Keeps the QEMU window titled and iconed as DexTor, and at least 640×480, for the whole session.
    /// </summary>
    /// <remarks>
    /// SDL rewrites its title on its own - on mouse grab and release, on pause - so a single
    /// rename would last until the first click. The window is checked a few times a second
    /// and corrected only when it differs, which costs one GetWindowText per check.
    /// SDL also sizes the window to every video mode the guest passes through - firmware,
    /// GRUB, the splash - which left a small window in the corner of the screen after a
    /// single maximize. During the boot phase the window is maximized again whenever that
    /// happens; afterwards the operator's own size is left alone.
    /// </remarks>
    public static async Task KeepBrandedAsync(IQemuProcess process, string title, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            var (bigIcon, smallIcon) = LoadDexTorIcons();
            var bootPhaseEnds = DateTime.UtcNow + BootPhase;
            while (!process.HasExited)
            {
                var window = FindMainWindow(process.Id);
                if (window != IntPtr.Zero)
                {
                    var current = ReadTitle(window);
                    var wanted = BrandTitle(current, title);
                    if (!string.Equals(current, wanted, StringComparison.Ordinal))
                    {
                        _ = SetWindowText(window, wanted);
                    }

                    // Never before the reveal: maximizing shows the window, and it is kept hidden
                    // until the guest draws.
                    if (DateTime.UtcNow < bootPhaseEnds && IsRevealed(process.Id) && !IsZoomed(window) && !IsIconic(window))
                    {
                        _ = ShowWindowAsync(window, ShowMaximized);
                    }

                    EnsureMinimumClientSize(window);

                    if (bigIcon != IntPtr.Zero && SendMessage(window, WmGetIcon, IconBig, IntPtr.Zero) != bigIcon)
                    {
                        _ = SendMessage(window, WmSetIcon, IconBig, bigIcon);
                        _ = SendMessage(window, WmSetIcon, IconSmall, smallIcon != IntPtr.Zero ? smallIcon : bigIcon);
                    }
                }

                await Task.Delay(interval).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Branding is cosmetic and must never disturb a running session.
        }
    }

    /// <summary>
    /// Whatever SDL wrote - "QEMU (name)", "name - Press Right-Ctrl-G to exit grab",
    /// "[Paused]" - the window is titled just <paramref name="title"/>.
    /// </summary>
    /// <remarks>
    /// The grab hint used to be kept, but testers read it as a raw emulator window. The
    /// pointer is a USB tablet, so it is never captured and needs no way out.
    /// </remarks>
    internal static string BrandTitle(string current, string title) => title;

    /// <summary>The smallest picture area the VM window is allowed to have.</summary>
    public const int MinimumClientWidth = 640;
    public const int MinimumClientHeight = 480;

    /// <summary>
    /// Grows the window when SDL has shrunk it below 640×480.
    /// </summary>
    /// <remarks>
    /// SDL sizes the window to the guest's current video mode and does so again on every
    /// mode change: firmware and GRUB modes smaller than 640×480 left a window in which
    /// the boot menu was squeezed and drifted. Only the picture area is measured, so the
    /// title bar and frame do not count; a maximized or minimized window is left alone,
    /// and a window is only ever grown, never shrunk.
    /// </remarks>
    private static void EnsureMinimumClientSize(IntPtr window)
    {
        if (IsZoomed(window) || IsIconic(window)) return;
        if (!GetClientRect(window, out var client) || !GetWindowRect(window, out var frame)) return;
        var target = MinimumWindowSize(
            client.Right - client.Left,
            client.Bottom - client.Top,
            frame.Right - frame.Left,
            frame.Bottom - frame.Top);
        if (target is { } size)
        {
            _ = SetWindowPos(window, IntPtr.Zero, 0, 0, size.Width, size.Height,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }
    }

    /// <summary>
    /// The outer window size that gives at least the minimum picture area, or null when the
    /// window is already large enough.
    /// </summary>
    internal static (int Width, int Height)? MinimumWindowSize(int clientWidth, int clientHeight, int windowWidth, int windowHeight)
    {
        if (clientWidth <= 0 || clientHeight <= 0) return null;
        if (clientWidth >= MinimumClientWidth && clientHeight >= MinimumClientHeight) return null;
        var width = windowWidth + Math.Max(0, MinimumClientWidth - clientWidth);
        var height = windowHeight + Math.Max(0, MinimumClientHeight - clientHeight);
        return (width, height);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    private static unsafe string ReadTitle(IntPtr window)
    {
        Span<char> buffer = stackalloc char[512];
        int length;
        fixed (char* text = buffer)
        {
            length = GetWindowText(window, text, buffer.Length);
        }
        return length <= 0 ? string.Empty : new string(buffer[..length]);
    }

    private static IntPtr s_bigIcon;
    private static IntPtr s_smallIcon;

    /// <summary>
    /// The icon of the running launcher executable, loaded once and kept for the process
    /// lifetime: the QEMU window holds only the handle, so it must stay valid.
    /// </summary>
    private static (IntPtr Big, IntPtr Small) LoadDexTorIcons()
    {
        if (s_bigIcon != IntPtr.Zero) return (s_bigIcon, s_smallIcon);
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return (IntPtr.Zero, IntPtr.Zero);
        if (ExtractIconEx(path, 0, out var big, out var small, 1) > 0)
        {
            s_bigIcon = big;
            s_smallIcon = small;
        }

        return (s_bigIcon, s_smallIcon);
    }

    private const uint WmSetIcon = 0x0080;
    private const uint WmGetIcon = 0x007F;
    private static readonly IntPtr IconSmall = IntPtr.Zero;
    private static readonly IntPtr IconBig = new(1);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(IntPtr window, char* text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowText(IntPtr window, string text);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string file, int index, out IntPtr largeIcon, out IntPtr smallIcon, uint icons);

    /// <summary>
    /// Walks the top-level window band in z-order. A sibling walk rather than
    /// <c>EnumWindows</c>: no callback has to be marshalled, and the loop is bounded.
    /// </summary>
    private static IntPtr FindMainWindow(int processId)
    {
        var window = GetTopWindow(IntPtr.Zero);
        for (var visited = 0; window != IntPtr.Zero && visited < MaximumWindowsToScan; visited++)
        {
            _ = GetWindowThreadProcessId(window, out var owner);
            if (owner == (uint)processId
                && IsWindowVisible(window)
                // An unowned top-level window is the closest equivalent of what
                // Process.MainWindowHandle reported.
                && GetWindow(window, GwOwner) == IntPtr.Zero)
            {
                return window;
            }

            window = GetWindow(window, GwHwndNext);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// QEMU's graphics window while it is still hidden: of the process's <c>SDL_app</c> windows,
    /// the one made first - the lowest in z-order, since nothing has been shown yet. The others
    /// belong to QEMU's text consoles and are never shown.
    /// </summary>
    private static IntPtr FindFirstSdlWindow(int processId)
    {
        var found = IntPtr.Zero;
        var window = GetTopWindow(IntPtr.Zero);
        for (var visited = 0; window != IntPtr.Zero && visited < MaximumWindowsToScan; visited++)
        {
            _ = GetWindowThreadProcessId(window, out var owner);
            if (owner == (uint)processId && GetWindow(window, GwOwner) == IntPtr.Zero && QemuWindowWatcher.IsSdlWindow(window))
            {
                found = window;
            }

            window = GetWindow(window, GwHwndNext);
        }

        return found;
    }

    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const int MaximumWindowsToScan = 4096;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(IntPtr window, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetTopWindow(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr GetWindow(IntPtr window, uint command);
}
