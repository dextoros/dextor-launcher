using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DexTor.SessionHost;

/// <summary>
/// Follows QEMU's main window as Windows reports its moves: notes which window it is, hides it
/// should it show before the guest draws, and while the guest boots puts it back to the full
/// screen whenever SDL shrinks it.
/// </summary>
/// <remarks>
/// <para>
/// SDL sizes its window to every video mode the guest passes through - firmware, GRUB, the
/// kernel, the desktop - and does so even while the window is maximized: the window keeps its
/// maximized state but takes the guest's 720×400 or 1024×768 in the top left corner. Polling
/// every 250 ms for a window that was not maximized missed that entirely; testers saw a small
/// black window in the corner that then grew to the screen. (Measured on QEMU 11.1: a
/// maximized 1936×1168 window went to 736×439 and stayed there, still "maximized".)
/// </para>
/// <para>
/// An out-of-context WinEvent hook on QEMU's process reports EVENT_OBJECT_SHOW and
/// EVENT_OBJECT_LOCATIONCHANGE as they happen, on a thread of the session host that only
/// pumps messages for it. The full-screen rectangle seen once the window was maximized is
/// remembered, and any smaller one while booting is put back to it.
/// </para>
/// <para>
/// QEMU creates one SDL window per console - the graphics one first, then hidden ones for its
/// text consoles - so the main window is the first <c>SDL_app</c> window the hook sees.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class QemuWindowWatcher : IDisposable
{
    internal enum Mode
    {
        /// <summary>Nothing done; the hook only notes the main window.</summary>
        Off,

        /// <summary>The guest has drawn nothing yet: the window is hidden should it show.</summary>
        Hidden,

        /// <summary>Booting: whenever the window is moved or shrunk, it fills the screen again.</summary>
        Maximized
    }

    internal enum WindowAction { None, Hide, Maximize, RestoreMaximizedBounds }

    /// <summary>A window rectangle in screen coordinates.</summary>
    internal readonly record struct Bounds(int Left, int Top, int Right, int Bottom)
    {
        internal long Area => (long)Math.Max(0, Right - Left) * Math.Max(0, Bottom - Top);
    }

    internal const string SdlWindowClass = "SDL_app";

    private readonly int _processId;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private volatile Mode _mode;
    private uint _threadId;
    private IntPtr _hook;
    private WinEventProc? _callback;
    private long _mainWindow;
    private Bounds? _maximizedBounds;

    private QemuWindowWatcher(int processId, Mode mode)
    {
        _processId = processId;
        _mode = mode;
        _thread = new Thread(Pump) { IsBackground = true, Name = "QEMU window watcher" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>Starts watching <paramref name="processId"/>'s windows; null if the hook cannot be set.</summary>
    internal static QemuWindowWatcher? Start(int processId, Mode mode)
    {
        var watcher = new QemuWindowWatcher(processId, mode);
        watcher._thread.Start();
        watcher._ready.Wait(TimeSpan.FromSeconds(2));
        if (watcher._hook != IntPtr.Zero) return watcher;
        watcher.Dispose();
        return null;
    }

    /// <summary>
    /// Called, on the watcher's thread, when SDL sizes the shown window to QEMU's 640x480
    /// placeholder: the guest has switched its display off for a moment (a mode change).
    /// </summary>
    internal Action? PlaceholderShown { get; set; }

    /// <summary>QEMU's graphics window, hidden or not, once the hook has seen it.</summary>
    internal IntPtr MainWindow => new(Interlocked.Read(ref _mainWindow));

    internal Mode Current
    {
        get => _mode;
        set => _mode = value;
    }

    /// <summary>
    /// What the window's picture area must be a multiple of, wide and high; null leaves its size
    /// alone. See <see cref="VmDisplayPlanner.Steps"/>.
    /// </summary>
    /// <remarks>
    /// QEMU tells the guest the picture area's size whenever the window changes size -
    /// maximized, restored or dragged - and DexTor OS takes that size (measured: asked for
    /// 2544x1350, it went to the 2560x1351 of the maximized window). A width that is not a
    /// multiple of 16 skews every frame; sides that the scale does not divide keep GNOME at
    /// 100%. So the window itself is fitted: a few pixels narrower or lower, centred when
    /// maximized, once a drag has ended or the window has changed state.
    /// </remarks>
    internal (int Width, int Height)? SnapSteps { get; set; }

    private volatile bool _sizing;

    /// <summary>How much a picture area of <paramref name="width"/> x <paramref name="height"/> is too large for the steps.</summary>
    internal static (int Width, int Height) Excess(int width, int height, (int Width, int Height) steps) =>
        (width <= steps.Width ? 0 : width % steps.Width, height <= steps.Height ? 0 : height % steps.Height);

    /// <summary>What to do with an event on the main window, given the mode and the window's state.</summary>
    internal static WindowAction Decide(Mode mode, bool visible, bool zoomed, bool iconic, Bounds bounds, Bounds? maximizedBounds)
    {
        if (!visible || iconic) return WindowAction.None;
        return mode switch
        {
            Mode.Hidden => WindowAction.Hide,
            Mode.Maximized when !zoomed => WindowAction.Maximize,
            Mode.Maximized when maximizedBounds is { } full && bounds != full && bounds.Area < full.Area => WindowAction.RestoreMaximizedBounds,
            _ => WindowAction.None
        };
    }

    private void Pump()
    {
        _callback = OnWinEvent;
        _threadId = GetCurrentThreadId();
        // QEMU's SDL window counts physical pixels; so does this thread.
        _ = SetThreadDpiAwarenessContext(new IntPtr(-4));
        _hook = SetWinEventHook(EventSystemMoveSizeStart, EventObjectLocationChange, IntPtr.Zero,
            Marshal.GetFunctionPointerForDelegate(_callback), (uint)_processId, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
        _ready.Set();
        if (_hook == IntPtr.Zero) return;
        try
        {
            while (GetMessageW(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(in message);
                _ = DispatchMessageW(in message);
            }
        }
        finally
        {
            _ = UnhookWinEvent(_hook);
        }
    }

    private void OnWinEvent(IntPtr hook, uint windowEvent, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            // Only top-level windows themselves, not their caret, cursor or children.
            if (windowEvent is not (EventObjectShow or EventObjectLocationChange or EventSystemMoveSizeStart or EventSystemMoveSizeEnd)) return;
            if (objectId != ObjIdWindow || childId != 0 || window == IntPtr.Zero) return;
            if (windowEvent is EventSystemMoveSizeStart or EventSystemMoveSizeEnd)
            {
                if (window != MainWindow) return;
                _sizing = windowEvent == EventSystemMoveSizeStart;
                if (!_sizing) Snap(window);
                return;
            }

            var main = MainWindow;
            if (main == IntPtr.Zero)
            {
                if (GetAncestor(window, GaRoot) != window || GetWindow(window, GwOwner) != IntPtr.Zero || !IsSdlWindow(window)) return;
                Interlocked.CompareExchange(ref _mainWindow, window.ToInt64(), 0);
                main = MainWindow;
            }

            if (window != main || !GetWindowRect(window, out var rect)) return;
            var bounds = new Bounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
            var zoomed = IsZoomed(window);
            if (_mode == Mode.Maximized && PlaceholderShown is { } placeholder && GetClientRect(window, out var client)
                && client.Right - client.Left == GuestDisplay.PlaceholderWidth && client.Bottom - client.Top == GuestDisplay.PlaceholderHeight)
            {
                placeholder();
            }

            if (_mode == Mode.Maximized && zoomed && (_maximizedBounds is not { } known || bounds.Area > known.Area))
            {
                // With fitting on, "full size" is the fitted size; learning the raw maximized
                // rectangle would have the two corrections undo each other.
                if (SnapSteps is null)
                {
                    _maximizedBounds = bounds;
                }
                else
                {
                    Snap(window);
                    return;
                }
            }

            switch (Decide(_mode, IsWindowVisible(window), zoomed, IsIconic(window), bounds, _maximizedBounds))
            {
                case WindowAction.Hide:
                    _ = ShowWindowAsync(window, SwHide);
                    break;
                case WindowAction.Maximize:
                    _ = ShowWindowAsync(window, SwShowMaximized);
                    break;
                case WindowAction.RestoreMaximizedBounds when _maximizedBounds is { } full:
                    _ = SetWindowPos(window, IntPtr.Zero, full.Left, full.Top, full.Right - full.Left, full.Bottom - full.Top,
                        SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
                    break;
                default:
                    // Not in the middle of a drag: the operator's own resize is fitted when it ends.
                    if (!_sizing) Snap(window);
                    break;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Window placement is cosmetic; it never disturbs the session.
        }
    }

    /// <summary>Makes the window a few pixels smaller where its picture area does not fit <see cref="SnapSteps"/>.</summary>
    private void Snap(IntPtr window)
    {
        if (SnapSteps is not { } steps || !IsWindowVisible(window) || IsIconic(window)) return;
        if (!GetClientRect(window, out var client) || !GetWindowRect(window, out var rect)) return;
        var (dx, dy) = Excess(client.Right - client.Left, client.Bottom - client.Top, steps);
        // A maximized window keeps the size Windows gives it - a smaller one is refused (measured
        // on Notepad and on QEMU) - so only a window the operator sized is fitted.
        var zoomed = IsZoomed(window);
        if (zoomed || (dx == 0 && dy == 0))
        {
            if (zoomed && _mode == Mode.Maximized) _maximizedBounds = new Bounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
            return;
        }

        // Maximized: centred, so the few pixels left over are shared by both sides.
        var left = zoomed ? rect.Left + dx / 2 : rect.Left;
        var fitted = new Bounds(left, rect.Top, left + rect.Right - rect.Left - dx, rect.Bottom - dy);
        if (zoomed) _maximizedBounds = fitted;
        _ = SetWindowPos(window, IntPtr.Zero, fitted.Left, fitted.Top, fitted.Right - fitted.Left, fitted.Bottom - fitted.Top,
            SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
    }

    internal static bool IsSdlWindow(IntPtr window)
    {
        Span<char> name = stackalloc char[32];
        var length = GetClassName(window, name);
        return length > 0 && name[..length].SequenceEqual(SdlWindowClass);
    }

    private static unsafe int GetClassName(IntPtr window, Span<char> buffer)
    {
        fixed (char* text = buffer)
        {
            return GetClassNameW(window, text, buffer.Length);
        }
    }

    public void Dispose()
    {
        _mode = Mode.Off;
        if (_thread.IsAlive && _threadId != 0)
        {
            _ = PostThreadMessageW(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
    }

    internal const uint EventSystemMoveSizeStart = 0x000A;
    internal const uint EventSystemMoveSizeEnd = 0x000B;
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectLocationChange = 0x800B;
    private const uint WinEventOutOfContext = 0x0000;
    private const uint WinEventSkipOwnProcess = 0x0002;
    private const int ObjIdWindow = 0;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;
    private const int SwHide = 0;
    private const int SwShowMaximized = 3;
    private const uint WmQuit = 0x0012;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpAsyncWindowPos = 0x4000;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProc(IntPtr hook, uint windowEvent, IntPtr window, int objectId, int childId, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Window;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, IntPtr callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out NativeMessage message, IntPtr window, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in NativeMessage message);

    [LibraryImport("user32.dll")]
    private static partial IntPtr DispatchMessageW(in NativeMessage message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetAncestor(IntPtr window, uint flags);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetWindow(IntPtr window, uint command);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(IntPtr window, char* className, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(IntPtr window, out NativeRect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsZoomed(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindowAsync(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
