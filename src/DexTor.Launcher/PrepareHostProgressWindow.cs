using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

using DexTor.Application;

namespace DexTor.Launcher;

internal sealed class PrepareHostProgressWindow : Form
{
    private readonly ILocalizer _localizer;
    private readonly Func<bool> _prepareCallback;
    private readonly TextBox _statusText;
    private readonly TextBox _resultText;
    private readonly SpinnerCanvas _spinnerCanvas;
    private readonly Button _closeButton;
    private readonly System.Windows.Forms.Timer _spinnerTimer;
    private readonly string _completeMessage;
    private readonly string _failureMessage;
    private bool _initializing = true;
    private bool _isRunning = true;
    private int _spinAngle;
    public bool Success { get; private set; }

    private PrepareHostProgressWindow(
        ILocalizer localizer,
        Func<bool> prepareCallback,
        string title,
        string subtitle,
        string completeMessage,
        string failureMessage)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _prepareCallback = prepareCallback ?? throw new ArgumentNullException(nameof(prepareCallback));
        _completeMessage = completeMessage;
        _failureMessage = failureMessage;

        Text = title;
        Width = 560;
        Height = 250;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = LauncherWindow.Theme.Background;
        DoubleBuffered = true;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.2f, FontStyle.Regular, GraphicsUnit.Point);

        Paint += (_, e) => PaintBackground(e.Graphics, ClientRectangle);
        FormClosing += (_, e) =>
        {
            if (_isRunning)
            {
                e.Cancel = true;
            }
        };

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(20),
            BackColor = Color.Transparent
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            Text = title,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextPrimary,
            AutoSize = false,
            Margin = new Padding(0, 0, 0, 8),
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };

        _statusText = new TextBox
        {
            Text = subtitle,
            Font = new Font(Font.FontFamily, 10.2f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextPrimary,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BorderStyle = BorderStyle.FixedSingle,
            Height = 62,
            Margin = new Padding(0, 0, 0, 10),
            BackColor = LauncherWindow.Theme.Surface,
            AcceptsReturn = true
        };

        var spinnerRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0)
        };

        _spinnerCanvas = new SpinnerCanvas
        {
            Size = new Size(40, 40),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 12, 0)
        };
        _spinnerCanvas.Paint += (_, e) => _spinnerCanvas.DrawSpinner(e.Graphics, _spinAngle);

        _resultText = new TextBox
        {
            Text = string.Empty,
            Font = new Font(Font.FontFamily, 10.4f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextMuted,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            BorderStyle = BorderStyle.None,
            Height = 40,
            Width = 430,
            BackColor = LauncherWindow.Theme.Surface,
            AcceptsReturn = true
        };

        _closeButton = new Button
        {
            Text = _localizer.T("launcher.close"),
            Size = new Size(130, 34),
            Enabled = false,
            BackColor = LauncherWindow.Theme.Surface,
            ForeColor = LauncherWindow.Theme.TextPrimary,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold, GraphicsUnit.Point),
            FlatStyle = FlatStyle.Flat
        };
        _closeButton.FlatAppearance.BorderSize = 1;
        _closeButton.FlatAppearance.BorderColor = LauncherWindow.Theme.CardBorder;
        _closeButton.Click += (_, _) => Close();

        spinnerRow.Controls.Add(_spinnerCanvas);
        spinnerRow.Controls.Add(_resultText);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            BackColor = Color.Transparent,
            WrapContents = false
        };
        footer.Controls.Add(_closeButton);

        root.Controls.Add(titleLabel, 0, 0);
        root.Controls.Add(_statusText, 0, 1);
        root.Controls.Add(spinnerRow, 0, 2);
        root.Controls.Add(footer, 0, 3);

        Controls.Add(root);
        LauncherBranding.Attach(this, localizer);
        _initializing = false;

        _spinnerTimer = new System.Windows.Forms.Timer { Interval = 90 };
        _spinnerTimer.Tick += (_, _) =>
        {
            _spinAngle += 25;
            if (_spinAngle >= 360)
            {
                _spinAngle -= 360;
            }
            _spinnerCanvas.Invalidate();
        };

        Shown += async (_, _) => await ExecutePrepareAsync();
        AcceptButton = _closeButton;
        _spinnerTimer.Start();
        UpdateTextHeights();
    }

    public static bool Run(
        ILocalizer localizer,
        Func<bool> prepareCallback,
        string title,
        string subtitle,
        string completeMessage,
        string failureMessage)
    {
        using var window = new PrepareHostProgressWindow(
            localizer,
            prepareCallback,
            title,
            subtitle,
            completeMessage,
            failureMessage);
        window.ShowDialog();
        return window.Success;
    }

    private async Task ExecutePrepareAsync()
    {
        try
        {
            _resultText.Text = _localizer.T("launcher.prepare_progress.running");
            var result = await Task.Run(_prepareCallback);
            Success = result;
        }
        finally
        {
            _isRunning = false;
            _spinnerTimer.Stop();
            _spinnerCanvas.Visible = false;
            _closeButton.Enabled = true;
            _statusText.Text = Success ? _completeMessage : _failureMessage;
            _resultText.Text = Success
                ? _localizer.T("launcher.prepare_progress.success")
                : _localizer.T("launcher.prepare_progress.fail");
            _resultText.ForeColor = Success
                ? Color.FromArgb(0xff, 0xa8, 0xff, 0xc4)
                : Color.FromArgb(0xff, 0xee, 0x7f, 0x87);
            _statusText.ForeColor = Success ? LauncherWindow.Theme.TextPrimary : Color.FromArgb(0xff, 0xed, 0x88, 0x93);
            UpdateTextHeights();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_initializing)
        {
            return;
        }

        UpdateTextHeights();
    }

    private void UpdateTextHeights()
    {
        if (_initializing || _statusText == null || _resultText == null || Width <= 0)
        {
            return;
        }

        var contentWidth = Math.Max(300, Width - 120);
        _statusText.Width = Math.Min(430, contentWidth);
        _resultText.Width = Math.Min(430, contentWidth);
        _statusText.Height = Math.Min(170, Math.Max(48, MeasureTextHeight(_statusText.Text, _statusText.Font, _statusText.Width)));
        _resultText.Height = Math.Min(84, Math.Max(34, MeasureTextHeight(_resultText.Text, _resultText.Font, _resultText.Width)));
    }

    private static int MeasureTextHeight(string text, Font font, int width)
    {
        var size = TextRenderer.MeasureText(
            text,
            font,
            new Size(width - 16, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        return Math.Max(18, size.Height + 12);
    }

    private static void PaintBackground(Graphics graphics, Rectangle bounds)
    {
        using var background = new LinearGradientBrush(
            bounds,
            LauncherWindow.Theme.BackgroundTop,
            LauncherWindow.Theme.BackgroundBottom,
            LinearGradientMode.Vertical);
        graphics.FillRectangle(background, bounds);
    }
}

internal sealed class SpinnerCanvas : Panel
{
    public void DrawSpinner(Graphics graphics, int angle)
    {
        if (!Visible || Width <= 0 || Height <= 0)
        {
            return;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var square = new Rectangle(4, 4, Width - 8, Height - 8);
        if (square.Width <= 0 || square.Height <= 0)
        {
            return;
        }

        var centerX = square.X + square.Width / 2f;
        var centerY = square.Y + square.Height / 2f;
        var radius = Math.Min(square.Width, square.Height) / 2f - 1f;

        var segmentCount = 12;
        for (var i = 0; i < segmentCount; i++)
        {
            var itemAngle = (float)(i * 360d / segmentCount + angle);
            var alpha = Math.Max(28, 255 - (i * 18));
            using var brush = new SolidBrush(Color.FromArgb(alpha, LauncherWindow.Theme.AccentSoft));
            using var pen = new Pen(brush, 4f);
            graphics.DrawLine(
                pen,
                centerX + (float)(Math.Cos((itemAngle - 90) * Math.PI / 180d) * (radius - 4)),
                centerY + (float)(Math.Sin((itemAngle - 90) * Math.PI / 180d) * (radius - 4)),
                centerX + (float)(Math.Cos((itemAngle - 90) * Math.PI / 180d) * radius),
                centerY + (float)(Math.Sin((itemAngle - 90) * Math.PI / 180d) * radius));
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
    }
}
