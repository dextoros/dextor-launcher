using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using DexTor.Application;

namespace DexTor.Launcher;

internal sealed class ActionMessageWindow : Form
{
    private const int ContentWidth = 700;
    private readonly ILocalizer _localizer;
    private readonly TextBox _messageText;
    private static readonly Color ErrorColor = Color.FromArgb(0xff, 0xee, 0x7f, 0x87);
    private static readonly Color WarningColor = Color.FromArgb(0xff, 0xfa, 0x96, 0x1d);
    private static readonly Color AccentColor = Color.FromArgb(0xff, 0xa0, 0x7c, 0xff);

    private ActionMessageWindow(string message, ILocalizer localizer, MessageBoxIcon icon)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));

        Text = _localizer.T("launcher.preflight.title");
        Width = 840;
        Height = 340;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = LauncherWindow.Theme.Background;
        ForeColor = LauncherWindow.Theme.TextPrimary;
        DoubleBuffered = true;
        Font = new Font(SystemFonts.MessageBoxFont!.FontFamily, 10.1f, FontStyle.Regular, GraphicsUnit.Point);

        Paint += (_, e) => PaintBackground(e.Graphics, ClientRectangle);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent,
            Padding = new Padding(20)
        };
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = BuildHeader(icon);
        var messageFont = new Font(Font.FontFamily, 10.4f, FontStyle.Regular, GraphicsUnit.Point);
        _messageText = new TextBox
        {
            Text = message,
            Font = messageFont,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Dock = DockStyle.Fill,
            Height = 140,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = LauncherWindow.Theme.Surface,
            ForeColor = LauncherWindow.Theme.TextPrimary,
            Margin = new Padding(0),
            AcceptsReturn = true
        };

        var close = CreateButton(_localizer.T("launcher.close"), LauncherWindow.Theme.Surface, LauncherWindow.Theme.TextPrimary);
        close.Click += (_, _) => Close();
        close.Width = 130;
        close.Height = 34;

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 52,
            FlowDirection = FlowDirection.RightToLeft,
            BackColor = Color.Transparent,
            WrapContents = false
        };
        footer.Controls.Add(close);

        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(_messageText, 0, 1);
        panel.Controls.Add(footer, 0, 2);
        Controls.Add(panel);
        LauncherBranding.Attach(this, localizer);

        AcceptButton = close;
        Resize += (_, _) => UpdateMessageLayout();
        UpdateMessageLayout();
    }

    public static void Show(ILocalizer localizer, string message, MessageBoxIcon icon)
    {
        using var window = new ActionMessageWindow(message, localizer, icon);
        window.ShowDialog();
    }

    private TableLayoutPanel BuildHeader(MessageBoxIcon icon)
    {
        var container = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            Height = 52,
            Width = ContentWidth + 20,
            BackColor = Color.Transparent,
            AutoSize = true
        };
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46f));
        container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        var iconGlyph = ResolveIconGlyph(icon);
        var iconColor = ResolveIconColor(icon);

        var iconLabel = new Label
        {
            Text = iconGlyph,
            Font = new Font(Font.FontFamily, 21f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = iconColor,
            AutoSize = false,
            Size = new Size(44, 44),
            TextAlign = ContentAlignment.MiddleCenter,
            BackColor = Color.Transparent
        };

        var titleLabel = new Label
        {
            Text = _localizer.T("launcher.preflight.title"),
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = LauncherWindow.Theme.TextPrimary,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent
        };

        container.Controls.Add(iconLabel, 0, 0);
        container.Controls.Add(titleLabel, 1, 0);
        return container;
    }

    private void UpdateMessageLayout()
    {
        var width = Math.Max(320, Math.Min(ContentWidth, ClientSize.Width - 56));
        _messageText.MaximumSize = new Size(width, 0);
        _messageText.Width = width;
        var fullHeight = Math.Max(40, TextRenderer.MeasureText(
            _messageText.Text,
            _messageText.Font,
            new Size(width - 14, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + 16);
        _messageText.Height = Math.Min(220, Math.Max(fullHeight, 80));
        _messageText.Multiline = true;
    }

    private static string ResolveIconGlyph(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => "✖",
        MessageBoxIcon.Warning => "⚠",
        MessageBoxIcon.Question => "❓",
        MessageBoxIcon.Information => "ⓘ",
        _ => "ⓘ"
    };

    private static Color ResolveIconColor(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => ErrorColor,
        MessageBoxIcon.Warning => WarningColor,
        MessageBoxIcon.Question => AccentColor,
        MessageBoxIcon.Information => AccentColor,
        _ => AccentColor
    };

    private static Button CreateButton(string text, Color backColor, Color foreColor)
    {
        var messageFont = SystemFonts.MessageBoxFont ?? new Font(FontFamily.GenericSansSerif, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        var button = new Button
        {
            Text = text,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font(messageFont.FontFamily, 9.5f, FontStyle.Bold, GraphicsUnit.Point),
            FlatStyle = FlatStyle.Flat,
            Height = 34,
            Width = 130,
            Margin = new Padding(0, 12, 0, 0),
            Padding = new Padding(0)
        };

        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = LauncherWindow.Theme.CardBorder;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(backColor, 0.15f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.25f);
        return button;
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
