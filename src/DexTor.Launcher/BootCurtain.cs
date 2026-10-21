using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DexTor.SessionHost;

namespace DexTor.Launcher;

/// <summary>
/// The launcher's splash while DexTor OS starts: the DexTor background, the wordmark and four
/// waiting dots, covering the work area of the monitor the VM window opens on.
/// </summary>
/// <remarks>
/// <para>
/// The VM window is shown, moved and maximized behind it and comes into view only once the
/// guest has drawn into it; until DexTor OS's desktop is up it covers the window again
/// whenever the guest's screen is QEMU's "Display output is not active" or black. See
/// QemuWindowManager.
/// </para>
/// <para>
/// It runs on a thread of its own, per-monitor DPI aware, so it covers the monitor in its
/// physical pixels and never waits for the wizard. It never takes the focus, has no taskbar
/// button and no opening animation.
/// </para>
/// </remarks>
internal sealed partial class BootCurtain : IBootCurtain, IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private CurtainForm? _form;

    public BootCurtain()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "DexTor boot curtain" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    private void Run()
    {
        _ = SetThreadDpiAwarenessContext(new IntPtr(-4));
        _form = new CurtainForm();
        _ = _form.Handle;
        _ready.Set();
        System.Windows.Forms.Application.Run(new ApplicationContext());
    }

    /// <summary>Whether the curtain is up, for the boot log and the tests.</summary>
    internal bool IsCovering => _form?.Covering ?? false;

    public void Cover(VmDisplay display)
    {
        if (_form is not { IsHandleCreated: true } form) return;
        form.BeginInvoke(() => form.CoverArea(display.WorkLeft, display.WorkTop, display.WorkRight, display.WorkBottom, display.ScalePercent));
    }

    public void Uncover()
    {
        if (_form is not { IsHandleCreated: true } form) return;
        form.BeginInvoke(form.Lift);
    }

    public void Dispose()
    {
        if (_form is { IsHandleCreated: true } form)
        {
            form.BeginInvoke(() =>
            {
                form.Close();
                System.Windows.Forms.Application.ExitThread();
            });
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private sealed partial class CurtainForm : Form
    {
        private static readonly Color Background = Color.FromArgb(0x0E, 0x0F, 0x13);
        private readonly Bitmap? _wordmark;
        private readonly System.Windows.Forms.Timer _dots = new() { Interval = 350 };
        private int _step;
        private float _scale = 1;

        internal volatile bool Covering;

        public CurtainForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Background;
            DoubleBuffered = true;
            Text = "DexTor OS";
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DexTor.Launcher.Assets.wordmark.png");
            _wordmark = stream is null ? null : new Bitmap(stream);
            _dots.Tick += (_, _) =>
            {
                _step = (_step + 1) % 4;
                Invalidate();
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                // No focus, no taskbar button, above the VM window.
                parameters.ExStyle |= WsExNoActivate | WsExToolWindow | WsExTopmost;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var disabled = 1;
            _ = DwmSetWindowAttribute(Handle, DwmwaTransitionsForceDisabled, ref disabled, sizeof(int));
        }

        internal void CoverArea(int left, int top, int right, int bottom, int scalePercent)
        {
            Covering = true;
            _scale = Math.Max(1, scalePercent / 100f);
            _ = SetWindowPos(Handle, HwndTopmost, left, top, right - left, bottom - top, SwpNoActivate | SwpShowWindow);
            _dots.Start();
            Invalidate();
        }

        internal void Lift()
        {
            Covering = false;
            _dots.Stop();
            _ = ShowWindow(Handle, SwHide);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Background);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var centre = new PointF(ClientSize.Width / 2f, ClientSize.Height / 2f);
            if (_wordmark is not null)
            {
                // The wordmark at 300 design px wide, from a 599 px source: never enlarged past it.
                var width = Math.Min(_wordmark.Width, 300 * _scale);
                var height = width * _wordmark.Height / _wordmark.Width;
                g.DrawImage(_wordmark, centre.X - width / 2, centre.Y - height / 2 - 24 * _scale, width, height);
            }

            // Four dots, one lit at a time, as DexTor OS's own splash has them.
            var radius = 4 * _scale;
            var gap = 16 * _scale;
            var y = centre.Y + 40 * _scale;
            for (var dot = 0; dot < 4; dot++)
            {
                var x = centre.X + (dot - 1.5f) * gap;
                using var brush = new SolidBrush(dot == _step ? Color.FromArgb(0x9B, 0x6C, 0xF2) : Color.FromArgb(0x3A, 0x34, 0x52));
                g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _dots.Dispose();
                _wordmark?.Dispose();
            }

            base.Dispose(disposing);
        }

        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExTopmost = 0x00000008;
        private const int DwmwaTransitionsForceDisabled = 3;
        private const uint SwpNoActivate = 0x0010;
        private const uint SwpShowWindow = 0x0040;
        private const int SwHide = 0;
        private static readonly IntPtr HwndTopmost = new(-1);

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ShowWindow(IntPtr window, int command);
    }
}
