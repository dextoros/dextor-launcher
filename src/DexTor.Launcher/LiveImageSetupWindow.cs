using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using DexTor.Application;

namespace DexTor.Launcher;

internal sealed class LiveImageSetupWindow : Form
{
    private readonly ILocalizer _localizer;
    private readonly Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImageCacheInspectionResult>> _inspectFlow;
    private readonly Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>> _prepareFlow;
    private readonly Func<bool> _hasWritableUsb;
    private readonly Label _headline;
    private readonly Label _description;
    private readonly Label _path;
    private readonly Label _status;
    private readonly Label _detail;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _refresh;
    private LiveImageCacheInspectionResult? _inspection;
    private LiveImagePreparationResult? _preparedImage;
    private bool _busy;
    private bool _acceptProgress;

    internal LiveImageSetupWindow(
        ILocalizer localizer,
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImageCacheInspectionResult>> inspectFlow,
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>> prepareFlow,
        Func<bool> hasWritableUsb)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _inspectFlow = inspectFlow ?? throw new ArgumentNullException(nameof(inspectFlow));
        _prepareFlow = prepareFlow ?? throw new ArgumentNullException(nameof(prepareFlow));
        _hasWritableUsb = hasWritableUsb ?? throw new ArgumentNullException(nameof(hasWritableUsb));

        Text = localizer.T("launcher.image_setup.window_title");
        Width = 940;
        Height = 740;
        MinimumSize = new Size(920, 640);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = LauncherWindow.Theme.Background;
        ForeColor = LauncherWindow.Theme.TextPrimary;
        DoubleBuffered = true;
        Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 10f);
        Paint += (_, e) => PaintBackground(e.Graphics, ClientRectangle);
        FormClosing += (_, e) => e.Cancel = _busy;

        var root = new TableLayoutPanel
        {
            Name = "ImageSetupRoot",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(24),
            BackColor = Color.Transparent
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = CreateLabel(localizer.T("launcher.image_setup.title"), 20f, FontStyle.Bold, LauncherWindow.Theme.TextPrimary);
        title.Margin = new Padding(0, 0, 0, 16);

        var card = new GlassPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            Margin = Padding.Empty,
            RevealProgress = 1f
        };
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            BackColor = Color.Transparent
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _headline = CreateLabel(localizer.T("launcher.image_setup.checking"), 13f, FontStyle.Bold, LauncherWindow.Theme.TextPrimary);
        _headline.Name = "ImageSetupHeadline";
        _description = CreateLabel(string.Empty, 10f, FontStyle.Regular, LauncherWindow.Theme.TextSecondary);
        _description.Name = "ImageSetupDescription";
        _description.Margin = new Padding(0, 12, 0, 0);
        _path = CreateLabel(string.Empty, 9f, FontStyle.Regular, LauncherWindow.Theme.TextMuted);
        _path.Name = "ImageSetupPath";
        _path.Margin = new Padding(0, 12, 0, 0);
        _status = CreateLabel(string.Empty, 9.5f, FontStyle.Bold, LauncherWindow.Theme.AccentSoft);
        _status.Name = "ImageSetupStatus";
        _status.Margin = new Padding(0, 16, 0, 0);
        _detail = CreateLabel(string.Empty, 9f, FontStyle.Regular, LauncherWindow.Theme.TextMuted);
        _detail.Name = "ImageSetupDetail";
        _detail.Margin = new Padding(0, 6, 0, 0);

        content.Controls.Add(_headline, 0, 0);
        content.Controls.Add(_description, 0, 1);
        content.Controls.Add(_path, 0, 2);
        content.Controls.Add(_status, 0, 3);
        content.Controls.Add(_detail, 0, 4);
        card.Controls.Add(content);

        _progress = new ProgressBar
        {
            Name = "ImageSetupProgress",
            Dock = DockStyle.Fill,
            Height = 12,
            Margin = new Padding(0, 16, 0, 4),
            Minimum = 0,
            Maximum = 100,
            Visible = false
        };

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Height = 54,
            BackColor = Color.Transparent
        };
        var close = CreateButton(localizer.T("launcher.close"), LauncherWindow.Theme.Surface, 128);
        close.Name = "ImageSetupCloseButton";
        close.Click += (_, _) => Close();
        _refresh = CreateButton(localizer.T("launcher.primary_action_refresh"), LauncherWindow.Theme.SurfaceLight, 150);
        _refresh.Name = "ImageSetupRefreshButton";
        _refresh.Click += async (_, _) => await InspectAsync();
        _primary = CreateButton(localizer.T("launcher.image_setup.download"), LauncherWindow.Theme.Accent, 220);
        _primary.Name = "ImageSetupPrimaryButton";
        _primary.Click += async (_, _) => await HandlePrimaryAsync();
        footer.Controls.Add(close);
        footer.Controls.Add(_refresh);
        footer.Controls.Add(_primary);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(card, 0, 1);
        root.Controls.Add(_progress, 0, 2);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        LauncherBranding.Attach(this, localizer);

        Shown += async (_, _) => await InspectAsync();
    }

    internal LiveImagePreparationResult? PreparedImage => _preparedImage;

    internal static LiveImagePreparationResult? Run(
        ILocalizer localizer,
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImageCacheInspectionResult>> inspectFlow,
        Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>> prepareFlow,
        Func<bool> hasWritableUsb)
    {
        using var window = new LiveImageSetupWindow(localizer, inspectFlow, prepareFlow, hasWritableUsb);
        return window.ShowDialog() == DialogResult.OK ? window.PreparedImage : null;
    }

    internal async Task InspectAsync()
    {
        SetBusy(true);
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        _headline.Text = _localizer.T("launcher.image_setup.checking");
        _description.Text = _localizer.T("launcher.image_setup.checking_details");
        _status.Text = _localizer.T(LiveImagePreparationService.StageResolveIndex);
        _status.ForeColor = LauncherWindow.Theme.AccentSoft;
        _detail.Text = string.Empty;

        var progress = new Progress<LiveImagePreparationProgress>(UpdateProgress);
        _acceptProgress = true;
        _inspection = await _inspectFlow(CancellationToken.None, progress);
        _acceptProgress = false;
        SetBusy(false);
        _progress.Visible = false;

        if (!_inspection.Success)
        {
            ShowError(_inspection.ErrorMessage);
            return;
        }

        _path.Text = _localizer.T("launcher.image_setup.cache_path", new Dictionary<string, string>
        {
            ["path"] = _inspection.CacheRoot
        });

        if (_inspection.IsReady)
        {
            _preparedImage = new LiveImagePreparationResult(
                true,
                _inspection.SourceUrl,
                _inspection.LocalImagePath,
                _inspection.SourceSha256,
                _inspection.SourceSizeBytes);
            ShowReady();
            return;
        }

        _preparedImage = null;
        _headline.Text = _localizer.T("launcher.image_setup.download_required");
        _description.Text = _localizer.T("launcher.image_setup.download_description");
        _status.Text = _localizer.T("launcher.image_setup.temp_ready");
        _status.ForeColor = LauncherWindow.Theme.AccentSoft;
        _detail.Text = string.Empty;
        _primary.Text = _localizer.T("launcher.image_setup.download");
        _primary.Enabled = true;
    }

    private async Task HandlePrimaryAsync()
    {
        if (_preparedImage?.Success == true)
        {
            if (!_hasWritableUsb())
            {
                _headline.Text = _localizer.T("launcher.image_setup.connect_usb");
                _description.Text = _localizer.T("launcher.image_setup.connect_usb_details");
                _status.Text = _localizer.T("launcher.image_setup.usb_not_found");
                _detail.Text = string.Empty;
                _primary.Text = _localizer.T("launcher.image_setup.check_usb");
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        await DownloadAsync();
    }

    private async Task DownloadAsync()
    {
        if (_inspection is null || !_inspection.Success)
        {
            await InspectAsync();
            return;
        }

        SetBusy(true);
        _progress.Visible = true;
        _progress.Style = ProgressBarStyle.Marquee;
        _headline.Text = _localizer.T("launcher.image_setup.downloading");
        _description.Text = _localizer.T("launcher.image_setup.downloading_details");
        var progress = new Progress<LiveImagePreparationProgress>(UpdateProgress);
        _acceptProgress = true;
        _preparedImage = await _prepareFlow(CancellationToken.None, progress);
        _acceptProgress = false;
        SetBusy(false);

        if (_preparedImage.Success)
        {
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = 100;
            ShowReady();
        }
        else
        {
            ShowError(_preparedImage.ErrorMessage);
            _preparedImage = null;
        }
    }

    private void ShowReady()
    {
        _progress.Visible = false;
        _headline.Text = _localizer.T("launcher.image_setup.ready");
        _description.Text = _localizer.T("launcher.image_setup.ready_details");
        _status.Text = _localizer.T("launcher.image_setup.verified");
        _status.ForeColor = Color.FromArgb(0xff, 0xa8, 0xff, 0xc4);
        _detail.Text = _preparedImage?.LocalImagePath ?? string.Empty;
        _primary.Text = _localizer.T("launcher.primary_action_continue");
        _primary.Enabled = true;
    }

    private void ShowError(string? error)
    {
        _progress.Visible = false;
        _headline.Text = _localizer.T("launcher.image_setup.failed");
        _description.Text = _localizer.T("launcher.image_setup.failed_details");
        _status.Text = error ?? _localizer.T("launcher.create.live_image.prepare.failed");
        _status.ForeColor = Color.FromArgb(0xff, 0xee, 0x7f, 0x87);
        _detail.Text = string.Empty;
        _primary.Enabled = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _primary.Enabled = !busy;
        _refresh.Enabled = !busy;
    }

    private void UpdateProgress(LiveImagePreparationProgress update)
    {
        if (!_acceptProgress)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(new Action<LiveImagePreparationProgress>(UpdateProgress), update);
            return;
        }

        _status.Text = _localizer.T(update.Stage);
        if (update.TotalBytes is { } total && total > 0)
        {
            var percent = (int)Math.Clamp(update.BytesDownloaded * 100d / total, 0, 100);
            _progress.Style = ProgressBarStyle.Blocks;
            _progress.Value = percent;
            var speed = update.BytesPerSecond is { } bytesPerSecond
                ? $" · {FormatBytes((long)bytesPerSecond)}/s"
                : string.Empty;
            var eta = update.EstimatedRemaining is { } remaining
                ? $" · {_localizer.T("launcher.create.live_image.prepare.eta")} {remaining:mm\\:ss}"
                : string.Empty;
            _detail.Text = $"{percent}% · {FormatBytes(update.BytesDownloaded)} / {FormatBytes(total)}{speed}{eta}";
        }
    }

    private static Label CreateLabel(string text, float size, FontStyle style, Color color)
    {
        return new Label
        {
            Text = text,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, size, style),
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(620, 0),
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };
    }

    private static Button CreateButton(string text, Color color, int width)
    {
        var button = new Button
        {
            Text = text,
            Width = width,
            Height = 36,
            Margin = new Padding(10, 10, 0, 0),
            BackColor = color,
            ForeColor = LauncherWindow.Theme.TextOnAccent,
            FlatStyle = FlatStyle.Flat,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 9.5f, FontStyle.Bold)
        };
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = LauncherWindow.Theme.CardBorder;
        return button;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.00", CultureInfo.InvariantCulture)} {units[unit]}";
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
