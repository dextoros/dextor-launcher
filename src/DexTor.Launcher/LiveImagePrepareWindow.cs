using System;
using System.Globalization;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DexTor.Application;
using FormsTimer = System.Windows.Forms.Timer;

namespace DexTor.Launcher;

internal sealed class LiveImagePrepareWindow : Form
{
    private readonly ILocalizer _localizer;
    private readonly Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>> _prepareFlow;
    private readonly ProgressBar _progressBar;
    private readonly Label _statusLabel;
    private readonly Label _detailLabel;
    private readonly Label _resultLabel;
    private readonly SpinnerCanvas _spinnerCanvas;
    private readonly Button _closeButton;
    private readonly FormsTimer _spinnerTimer;
    private int _spinAngle;
    private int _lastSpinAngle;
    private int _progressPercent;
    private bool _isRunning = true;
    public LiveImagePreparationResult? Result { get; private set; }

    private LiveImagePrepareWindow(
        ILocalizer localizer,
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>> prepareFlow,
        string title,
        string subtitle)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _prepareFlow = prepareFlow ?? throw new ArgumentNullException(nameof(prepareFlow));

        Text = title;
        Width = 680;
        Height = 260;
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
            RowCount = 6,
            Padding = new Padding(20),
            BackColor = Color.Transparent
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            Text = title,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextPrimary,
            AutoSize = false,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 0, 0, 6),
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent
        };

        var subtitleLabel = new Label
        {
            Text = subtitle,
            Font = new Font(Font.FontFamily, 10.0f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextSecondary,
            AutoSize = false,
            MaximumSize = new Size(620, 0),
            Height = 0,
            Margin = new Padding(0, 0, 0, 12),
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent
        };

        var headerRow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
            WrapContents = false,
            Padding = Padding.Empty
        };

        _spinnerCanvas = new SpinnerCanvas
        {
            Size = new Size(40, 40),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 10, 0)
        };
        _spinnerCanvas.Paint += (_, e) => _spinnerCanvas.DrawSpinner(e.Graphics, _spinAngle);

        _resultLabel = new Label
        {
            Text = _localizer.T("launcher.create.live_image.prepare.status.prepare"),
            Font = new Font(Font.FontFamily, 10.1f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextMuted,
            AutoSize = false,
            MaximumSize = new Size(540, 0),
            Dock = DockStyle.Top,
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent,
            Height = 28
        };

        headerRow.Controls.Add(_spinnerCanvas);
        headerRow.Controls.Add(_resultLabel);

        _statusLabel = new Label
        {
            Text = _localizer.T("launcher.create.live_image.prepare.status.prepare"),
            Font = new Font(Font.FontFamily, 10.2f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextPrimary,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 10, 0, 0),
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent
        };

        _detailLabel = new Label
        {
            Text = string.Empty,
            Font = new Font(Font.FontFamily, 9.2f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextMuted,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            Margin = new Padding(0, 8, 0, 6),
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent
        };

        _progressBar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 12,
            Minimum = 0,
            Maximum = 100,
            Style = ProgressBarStyle.Blocks,
            MarqueeAnimationSpeed = 0
        };

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 44,
            BackColor = Color.Transparent,
            WrapContents = false
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
        footer.Controls.Add(_closeButton);

        root.Controls.Add(titleLabel, 0, 0);
        root.Controls.Add(subtitleLabel, 0, 1);
        root.Controls.Add(_progressBar, 0, 2);
        root.Controls.Add(headerRow, 0, 3);
        root.Controls.Add(_detailLabel, 0, 4);
        root.Controls.Add(footer, 0, 5);
        root.SetRow(_detailLabel, 4);
        Controls.Add(root);
        LauncherBranding.Attach(this, localizer);

        _closeButton.Location = new Point(Width - 158, Height - 80);
        _statusLabel.Location = new Point(68, 140);
        headerRow.Location = new Point(0, 150);
        _detailLabel.Location = new Point(20, 180);

        var layout = new Label
        {
            Text = string.Empty
        };
        Controls.Add(layout);

        AcceptButton = _closeButton;
        Shown += async (_, _) =>
        {
            _statusLabel.Text = _localizer.T("launcher.create.live_image.prepare.status.prepare");
            await ExecutePrepareAsync();
        };

        _spinnerTimer = new FormsTimer { Interval = 90 };
        _spinnerTimer.Tick += (_, _) =>
        {
            if (_isRunning)
            {
                _spinAngle = (_spinAngle + 22) % 360;
                if (Math.Abs(_spinAngle - _lastSpinAngle) > 20)
                {
                    _spinnerCanvas.Invalidate();
                    _lastSpinAngle = _spinAngle;
                }
            }
        };
        _spinnerTimer.Start();
        SetSizeHints();
    }

    public static LiveImagePreparationResult Run(
        ILocalizer localizer,
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>> prepareFlow,
        string title,
        string subtitle)
    {
        using var window = new LiveImagePrepareWindow(localizer, prepareFlow, title, subtitle);
        window.ShowDialog();
        return window.Result ?? new LiveImagePreparationResult(
            false,
            null,
            null,
            null,
            0,
            "Preparation was cancelled.");
    }

    private async Task ExecutePrepareAsync()
    {
        var progress = new Progress<LiveImagePreparationProgress>(UpdateProgress);
        try
        {
            Result = await _prepareFlow(CancellationToken.None, progress);
            if (Result is null)
            {
                Result = new LiveImagePreparationResult(false, null, null, null, 0, "Unknown error.");
            }
        }
        catch (Exception exception)
        {
            Result = new LiveImagePreparationResult(false, null, null, null, 0, exception.Message);
        }
        finally
        {
            _isRunning = false;
            _spinnerTimer.Stop();
            _spinnerCanvas.Visible = false;
            _closeButton.Enabled = true;

            if (Result!.Success)
            {
                _statusLabel.Text = _localizer.T("launcher.create.live_image.prepare.done");
                _resultLabel.ForeColor = Color.FromArgb(0xff, 0xa8, 0xff, 0xc4);
                _resultLabel.Text = _localizer.T("launcher.create.live_image.prepare.done");
                _progressBar.Value = _progressBar.Maximum;
            }
            else
            {
                _statusLabel.Text = _localizer.T("launcher.create.live_image.prepare.failed");
                _statusLabel.ForeColor = Color.FromArgb(0xff, 0xee, 0x7f, 0x87);
                _resultLabel.ForeColor = Color.FromArgb(0xff, 0xee, 0x7f, 0x87);
                _resultLabel.Text = !string.IsNullOrWhiteSpace(Result!.ErrorMessage)
                    ? Result.ErrorMessage
                    : _localizer.T("launcher.create.live_image.prepare.failed");
                _detailLabel.Text = string.Empty;
                _progressBar.Style = ProgressBarStyle.Marquee;
            }
        }
    }

    private void UpdateProgress(LiveImagePreparationProgress progress)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<LiveImagePreparationProgress>(UpdateProgress), progress);
            return;
        }

        var stageText = ResolveStageLabel(progress.Stage);
        var speedText = progress.BytesPerSecond is null
            ? string.Empty
            : $" | {_localizer.T("launcher.create.live_image.prepare.speed")} {FormatSpeed(progress.BytesPerSecond.Value)}";
        var etaText = progress.EstimatedRemaining is null
            ? string.Empty
            : $" | {_localizer.T("launcher.create.live_image.prepare.eta")} {FormatEta(progress.EstimatedRemaining.Value)}";
        var totalText = progress.TotalBytes is null
            ? _localizer.T("launcher.create.live_image.prepare.total_unknown")
            : string.Format(
                CultureInfo.InvariantCulture,
                _localizer.T("launcher.create.live_image.prepare.total"),
                FormatBytes(progress.TotalBytes.Value));

        _statusLabel.Text = stageText;
        _detailLabel.Text = $"{localizerMaybe(progress.BytesDownloaded)} / {totalText}{speedText}{etaText}".Trim();

        if (progress.TotalBytes is { } total && total > 0)
        {
            _progressPercent = (int)Math.Clamp(progress.BytesDownloaded * 100d / total, 0, 100);
            _progressBar.Style = ProgressBarStyle.Blocks;
            _progressBar.Value = _progressPercent;
            if (_resultLabel.Text != _localizer.T("launcher.create.live_image.prepare.done"))
            {
                _resultLabel.Text = $"{_progressPercent}%";
            }
        }
        else
        {
            _progressBar.Style = ProgressBarStyle.Marquee;
        }
    }

    private static string ResolveStageLabel(string stage)
    {
        return stage switch
        {
            "launcher.create.live_image.prepare.status.resolve" => "launcher.create.live_image.prepare.status.resolve",
            "launcher.create.live_image.prepare.status.resolve_integrity" => "launcher.create.live_image.prepare.status.resolve_integrity",
            "launcher.create.live_image.prepare.status.verify_cache" => "launcher.create.live_image.prepare.status.verify_cache",
            "launcher.create.live_image.prepare.status.downloading" => "launcher.create.live_image.prepare.status.downloading",
            "launcher.create.live_image.prepare.status.verify_sha256" => "launcher.create.live_image.prepare.status.verify_sha256",
            "launcher.create.live_image.prepare.status.prepare" => "launcher.create.live_image.prepare.status.prepare",
            _ => stage.StartsWith("launcher.create.live_image.prepare.status.downloading.", StringComparison.Ordinal)
                ? "launcher.create.live_image.prepare.status.downloading"
                : "launcher.create.live_image.prepare.status.downloading"
        };
    }

    private static string FormatSpeed(double speedBytesPerSecond)
    {
        return $"{FormatBytes((long)speedBytesPerSecond)}/s";
    }

    private static string FormatEta(TimeSpan eta)
    {
        return eta < TimeSpan.FromHours(1)
            ? eta.ToString("mm\\:ss", CultureInfo.InvariantCulture)
            : eta.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long bytes)
    {
        var units = new[]
        {
            "B", "KB", "MB", "GB", "TB"
        };

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.00} {units[unit]}";
    }

    private static string localizerMaybe(long bytes)
    {
        return FormatBytes(bytes);
    }

    private void SetSizeHints()
    {
        _statusLabel.Width = Math.Max(220, ClientSize.Width - 50);
        _detailLabel.Width = Math.Max(220, ClientSize.Width - 52);
        _progressBar.Width = Math.Max(220, ClientSize.Width - 40);
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
