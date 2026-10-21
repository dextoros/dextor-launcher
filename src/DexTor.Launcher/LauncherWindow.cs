using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading.Tasks;
using System.Windows.Forms;

using DexTor.Application;

namespace DexTor.Launcher;

public sealed class LauncherWindow : Form
{
    private const int InlinePreparePanelHeight = 116;
    private const int WindowPadding = 18;
    private const int WindowDefaultWidth = 940;
    private const int WindowContentMaxWidth = 900;
    private const int WindowCardMinWidth = 620;
    private const int WindowBodyMarginBottom = 14;
    private readonly ILocalizer _localizer;
    private readonly LauncherPreflightPresentation _model;
    private readonly Func<Task<LauncherInlineActionResult>>? _prepareAction;
    private readonly List<GlassPanel> _cards = [];
    private readonly List<Button> _footerButtons = [];
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 16 };
    private readonly System.Windows.Forms.Timer _inlineSpinnerTimer = new() { Interval = 90 };
    private SpinnerCanvas? _inlineSpinner;
    private Label? _inlineTitle;
    private Label? _inlineDetail;
    private Control? _inlinePreparePanel;
    private FlowLayoutPanel? _requirementsPanel;
    private Button? _primaryButton;
    private float _fadeProgress;
    private int _frame;
    private int _inlineSpinAngle;
    private bool _isInlineActionRunning;
    public LauncherPrimaryAction SelectedPrimaryAction { get; private set; }

    public LauncherWindow(
        LauncherPreflightPresentation model,
        ILocalizer localizer,
        Func<Task<LauncherInlineActionResult>>? prepareAction = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _prepareAction = prepareAction;
        SelectedPrimaryAction = model.PrimaryAction;

        _animationTimer.Tick += (_, _) => TickAnimations();
        _inlineSpinnerTimer.Tick += (_, _) =>
        {
            _inlineSpinAngle = (_inlineSpinAngle + 25) % 360;
            _inlineSpinner?.Invalidate();
        };
        InitializeWindow(localizer);

        Load += (_, _) =>
        {
            Opacity = 0;
            _animationTimer.Start();
        };
        FormClosing += (_, e) =>
        {
            if (_isInlineActionRunning)
            {
                e.Cancel = true;
            }
        };
    }

    private void InitializeWindow(ILocalizer localizer)
    {
        Text = _model.WindowTitle;
        Width = WindowDefaultWidth;
        Height = 740;
        MinimumSize = new Size(920, 640);
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        DoubleBuffered = true;
        Font = CreateThemeFont(10f, FontStyle.Regular);

        Paint += (_, e) => PaintGlassBackground(e.Graphics, ClientRectangle);

        var root = new TableLayoutPanel
        {
            Name = "LauncherRoot",
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(WindowPadding),
            AutoSize = true,
            BackColor = Color.Transparent
        };

        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var contentWidth = ResolveContentWidth();
        var header = BuildHeader(contentWidth);
        var requirements = BuildRequirements();
        requirements.Dock = DockStyle.Fill;
        _requirementsPanel = requirements;
        var body = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        body.Controls.Add(requirements);
        _inlinePreparePanel = BuildInlinePreparePanel(contentWidth);
        body.Controls.Add(_inlinePreparePanel);
        body.SizeChanged += (_, _) => PositionInlinePreparePanel(body);
        PositionInlinePreparePanel(body);
        var footer = BuildFooter(localizer);
        footer.Dock = DockStyle.Fill;

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(body, 0, 1);
        root.Controls.Add(footer, 0, 2);

        Controls.Add(root);
        LauncherBranding.Attach(this, localizer);
    }

    private bool ShouldShowInlinePreparePanel =>
        _model.PrimaryAction == LauncherPrimaryAction.Prepare && _prepareAction is not null;

    private Panel BuildHeader(int contentWidth)
    {
        var header = new Panel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            BackColor = Color.Transparent
        };

        var title = new Label
        {
            Text = _model.Header,
            Font = new Font(Font.FontFamily, 20f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(0, 0, 0, 8),
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent,
            MaximumSize = new Size(contentWidth, 0),
            Width = contentWidth
        };

        var subtitle = new Label
        {
            Text = _model.SubHeader,
            Font = new Font(Font.FontFamily, 10.3f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Theme.TextSecondary,
            AutoSize = false,
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(2, 0, 0, 4),
            UseCompatibleTextRendering = true,
            BackColor = Color.Transparent,
            MaximumSize = new Size(contentWidth, 0),
            Width = contentWidth,
            AutoEllipsis = false
        };

        var separator = new Label
        {
            Height = 1,
            Width = contentWidth,
            BackColor = Theme.Stroke,
            Margin = new Padding(0, 8, 0, 6)
        };

        var titleRow = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent
        };
        titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        titleRow.Controls.Add(title, 0, 0);
        titleRow.Controls.Add(separator, 0, 1);
        titleRow.Controls.Add(subtitle, 0, 2);
        header.Controls.Add(titleRow);

        return header;
    }

    private FlowLayoutPanel BuildRequirements()
    {
        var contentWidth = ResolveContentWidth(reserveVerticalScrollbar: true);
        var requirementsPanel = new FlowLayoutPanel
        {
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            AutoSize = false,
            Padding = new Padding(0, 2, 0, 0),
            Margin = new Padding(0, 0, 0, WindowBodyMarginBottom)
        };

        var i = 0;
        foreach (var row in _model.Requirements)
        {
            var card = CreateRequirementCard(row, i, contentWidth);
            requirementsPanel.Controls.Add(card);
            _cards.Add(card);
            i++;
        }

        if (_model.Requirements.Count == 0)
        {
            var empty = new Panel
            {
                Width = contentWidth,
                Height = 80,
                Margin = new Padding(0, 0, 0, 8),
                BackColor = Theme.Surface,
                BorderStyle = BorderStyle.FixedSingle
            };
            var text = new Label
            {
                Text = _model.SubHeader,
                Dock = DockStyle.Fill,
                ForeColor = Theme.TextPrimary,
                Font = new Font(Font.FontFamily, 10f, FontStyle.Regular, GraphicsUnit.Point),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 12, 0),
                BackColor = Color.Transparent
            };
            empty.Controls.Add(text);
            requirementsPanel.Controls.Add(empty);
        }

        return requirementsPanel;
    }

    private GlassPanel CreateRequirementCard(LauncherRequirementRow row, int index, int contentWidth)
    {
        var titleFont = new Font(Font.FontFamily, 10.2f, FontStyle.Bold, GraphicsUnit.Point);
        var descriptionFont = new Font(Font.FontFamily, 9.4f, FontStyle.Regular, GraphicsUnit.Point);
        var infoFont = new Font(Font.FontFamily, 8.9f, FontStyle.Regular, GraphicsUnit.Point);
        var solutionFont = new Font(Font.FontFamily, 9.1f, FontStyle.Regular, GraphicsUnit.Point);
        var linkFont = new Font(Font.FontFamily, 9.1f, FontStyle.Regular, GraphicsUnit.Point);
        var innerWidth = contentWidth - 24;
        var contentTextWidth = Math.Max(220, contentWidth - 132);
        var hasSolution = !string.IsNullOrWhiteSpace(row.SolutionText);
        var hasInfo = !string.IsNullOrWhiteSpace(row.InfoText);
        var hasAction = !string.IsNullOrWhiteSpace(row.ActionText) && !string.IsNullOrWhiteSpace(row.ActionUrl);

        var top = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = Padding.Empty
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108f));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        top.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var status = new Label
        {
            Text = row.StateLabel,
            Font = new Font(Font.FontFamily, 9f, FontStyle.Bold, GraphicsUnit.Point),
            BackColor = Theme.StatusBadge(row.State),
            ForeColor = Theme.TextOnAccent,
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Margin = Padding.Empty,
            MinimumSize = new Size(108, 28),
            Size = new Size(108, 28),
            UseCompatibleTextRendering = true
        };

        var title = new Label
        {
            Text = row.Title,
            Font = titleFont,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Dock = DockStyle.Fill,
            MinimumSize = new Size(contentTextWidth, 28),
            AutoEllipsis = false,
            MaximumSize = new Size(contentTextWidth, 0),
            Padding = new Padding(8, 2, 0, 6),
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true,
            TextAlign = ContentAlignment.MiddleLeft
        };

        top.Controls.Add(status, 0, 0);
        top.Controls.Add(title, 1, 0);

        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Width = innerWidth,
            Padding = Padding.Empty,
            Margin = new Padding(0, 6, 0, 0),
            BackColor = Color.Transparent
        };

        if (!string.IsNullOrWhiteSpace(row.Description))
        {
            body.Controls.Add(CreateTextBlock(row.Description, descriptionFont, Theme.TextPrimary, innerWidth, Padding.Empty));
        }

        if (hasInfo)
        {
            body.Controls.Add(CreateTextBlock(_localizer.T("launcher.info.label"), infoFont, Theme.AccentSoft, innerWidth, new Padding(0, 10, 0, 3), FontStyle.Bold));
            body.Controls.Add(CreateTextBlock(row.InfoText!, infoFont, Theme.TextSecondary, innerWidth, Padding.Empty));
        }

        if (hasSolution)
        {
            body.Controls.Add(CreateTextBlock(_localizer.T("launcher.solution.label"), solutionFont, Theme.AccentSoft, innerWidth, new Padding(0, 10, 0, 3), FontStyle.Bold));
            body.Controls.Add(CreateTextBlock(row.SolutionText!, solutionFont, Theme.TextPrimary, innerWidth, Padding.Empty));
        }

        if (hasAction)
        {
            var action = new LinkLabel
            {
                Text = row.ActionText,
                Tag = row.ActionUrl,
                Font = linkFont,
                LinkColor = Theme.AccentSoft,
                ActiveLinkColor = Theme.TextOnAccent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                AutoSize = true,
                MaximumSize = new Size(innerWidth, 0),
                Margin = new Padding(0, 8, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                BackColor = Color.Transparent,
                UseCompatibleTextRendering = true
            };

            action.LinkClicked += (_, _) =>
            {
                if (action.Tag is string url)
                {
                    OpenUrl(url);
                }
            };

            body.Controls.Add(action);
        }

        var card = new GlassPanel
        {
            Width = contentWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(Math.Max(WindowCardMinWidth, contentWidth), 92),
            Margin = new Padding(0, 0, 0, 10),
            Padding = new Padding(12, 8, 12, 10),
            RevealDelay = index * 6,
            RevealProgress = 1f
        };

        var cardLayout = new TableLayoutPanel
        {
            Name = "RequirementCardLayout",
            ColumnCount = 1,
            RowCount = 2,
            Width = innerWidth,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent
        };
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        cardLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.Name = "RequirementHeader";
        body.Name = "RequirementBody";
        cardLayout.Controls.Add(top, 0, 0);
        cardLayout.Controls.Add(body, 0, 1);
        card.Controls.Add(cardLayout);
        return card;
    }

    private static Label CreateTextBlock(
        string text,
        Font font,
        Color color,
        int maxWidth,
        Padding margin,
        FontStyle style = FontStyle.Regular)
    {
        return new Label
        {
            Text = string.IsNullOrWhiteSpace(text) ? string.Empty : text,
            Font = new Font(font.FontFamily, font.Size, style, GraphicsUnit.Point),
            ForeColor = color,
            AutoSize = true,
            MaximumSize = new Size(maxWidth, 0),
            MinimumSize = new Size(maxWidth, 0),
            Margin = margin,
            TextAlign = ContentAlignment.TopLeft,
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };
    }

    private Control BuildInlinePreparePanel(int contentWidth)
    {
        if (!ShouldShowInlinePreparePanel)
        {
            return new Panel
            {
                Visible = false,
                Margin = Padding.Empty
            };
        }

        var panel = new GlassPanel
        {
            Name = "InlinePreparePanel",
            Width = contentWidth,
            Height = InlinePreparePanelHeight - 10,
            Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom,
            Visible = false,
            Margin = new Padding(0, 4, 0, 6),
            Padding = new Padding(14, 12, 14, 12),
            RevealProgress = 1f
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54f));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        _inlineSpinner = new SpinnerCanvas
        {
            Name = "InlinePrepareSpinner",
            Size = new Size(42, 42),
            Anchor = AnchorStyles.None,
            BackColor = Color.Transparent,
            Visible = false,
            Margin = Padding.Empty
        };
        _inlineSpinner.Paint += (_, e) => _inlineSpinner.DrawSpinner(e.Graphics, _inlineSpinAngle);

        _inlineTitle = new Label
        {
            Name = "InlinePrepareTitle",
            Text = _localizer.T("launcher.prepare_progress.title"),
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = new Font(Font.FontFamily, 11f, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = Theme.TextPrimary,
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true
        };

        _inlineDetail = new Label
        {
            Name = "InlinePrepareDetail",
            Text = _localizer.T("launcher.prepare_progress.subtitle"),
            Dock = DockStyle.Fill,
            AutoSize = false,
            Font = new Font(Font.FontFamily, 9.2f, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = Theme.TextSecondary,
            TextAlign = ContentAlignment.TopLeft,
            BackColor = Color.Transparent,
            UseCompatibleTextRendering = true,
            AutoEllipsis = true,
            Padding = new Padding(0, 3, 0, 0)
        };

        layout.Controls.Add(_inlineSpinner, 0, 0);
        layout.SetRowSpan(_inlineSpinner, 2);
        layout.Controls.Add(_inlineTitle, 1, 0);
        layout.Controls.Add(_inlineDetail, 1, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private void PositionInlinePreparePanel(Control body)
    {
        if (_inlinePreparePanel is null || !ShouldShowInlinePreparePanel)
        {
            return;
        }

        var height = InlinePreparePanelHeight - 10;
        _inlinePreparePanel.Bounds = new Rectangle(
            0,
            Math.Max(0, body.ClientSize.Height - height),
            body.ClientSize.Width,
            height);
    }

    private async Task RunInlinePrepareAsync()
    {
        if (_prepareAction is null || _isInlineActionRunning || _inlineTitle is null || _inlineDetail is null)
        {
            return;
        }

        _isInlineActionRunning = true;
        if (_inlinePreparePanel is not null)
        {
            _inlinePreparePanel.Visible = true;
            _inlinePreparePanel.BringToFront();
        }

        if (_requirementsPanel is not null)
        {
            _requirementsPanel.Padding = new Padding(0, 2, 0, InlinePreparePanelHeight);
        }

        UseWaitCursor = true;
        foreach (var button in _footerButtons)
        {
            button.Enabled = false;
        }
        foreach (Control selector in Controls.Find("DexTorLanguageSelector", true))
        {
            selector.Enabled = false;
        }

        _inlineTitle.Text = _localizer.T("launcher.prepare_progress.title");
        _inlineTitle.ForeColor = Theme.TextPrimary;
        _inlineDetail.Text = _localizer.T("launcher.prepare_progress.running");
        _inlineDetail.ForeColor = Theme.TextSecondary;
        if (_inlineSpinner is not null)
        {
            _inlineSpinner.Visible = true;
        }

        _inlineSpinnerTimer.Start();

        LauncherInlineActionResult result;
        try
        {
            result = await _prepareAction();
        }
        catch
        {
            result = new LauncherInlineActionResult(
                false,
                _localizer.T("launcher.prepare_progress.failed"),
                BuildInlineResultText(
                    _localizer.T("launcher.action.prepare.failed"),
                    _localizer.T("launcher.action.prepare.failed.solution")));
        }
        finally
        {
            _inlineSpinnerTimer.Stop();
            if (_inlineSpinner is not null)
            {
                _inlineSpinner.Visible = false;
            }

            UseWaitCursor = false;
            _isInlineActionRunning = false;
        }

        _inlineTitle.Text = result.Title;
        _inlineTitle.ForeColor = result.Success
            ? Color.FromArgb(0xff, 0xa8, 0xff, 0xc4)
            : Color.FromArgb(0xff, 0xee, 0x7f, 0x87);
        _inlineDetail.Text = result.Detail;
        _inlineDetail.ForeColor = result.Success ? Theme.TextPrimary : Color.FromArgb(0xff, 0xed, 0x88, 0x93);

        foreach (var button in _footerButtons)
        {
            button.Enabled = true;
        }
        foreach (Control selector in Controls.Find("DexTorLanguageSelector", true))
        {
            selector.Enabled = true;
        }

        if (_primaryButton is not null)
        {
            _primaryButton.Enabled = _model.CanContinue && !result.Success;
        }
    }

    private string BuildInlineResultText(string message, string solution)
    {
        return $"{message}  {_localizer.T("launcher.solution.label")} {solution}";
    }

    private FlowLayoutPanel BuildFooter(ILocalizer localizer)
    {
        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Height = 64,
            BackColor = Color.Transparent,
            AutoSize = false,
            WrapContents = false
        };

        var refresh = CreateActionButton(localizer.T("launcher.primary_action_refresh"), Theme.SurfaceLight, Theme.TextPrimary, true);
        refresh.Name = "RefreshButton";
        refresh.Width = 160;
        refresh.Height = 34;
        refresh.Click += (_, _) =>
        {
            DialogResult = DialogResult.Retry;
        };

        var primary = CreateActionButton(_model.PrimaryActionLabel, Theme.Accent, Theme.TextOnAccent, _model.CanContinue);
        primary.Name = "PrimaryActionButton";
        _primaryButton = primary;
        primary.Width = 204;
        primary.Height = 34;
        primary.Click += async (_, _) =>
        {
            SelectedPrimaryAction = _model.PrimaryAction;
            if (_model.PrimaryAction == LauncherPrimaryAction.Prepare && _prepareAction is not null)
            {
                await RunInlinePrepareAsync();
                return;
            }

            switch (_model.PrimaryAction)
            {
                case LauncherPrimaryAction.Retry:
                    DialogResult = DialogResult.Retry;
                    break;
                case LauncherPrimaryAction.ConnectDexTorUsb:
                case LauncherPrimaryAction.CreateDexTorUsb:
                case LauncherPrimaryAction.StartDexTor:
                case LauncherPrimaryAction.CheckAndRecover:
                case LauncherPrimaryAction.RepairAndStart:
                case LauncherPrimaryAction.OpenDexTor:
                case LauncherPrimaryAction.EndSession:
                case LauncherPrimaryAction.EjectUsb:
                case LauncherPrimaryAction.RestartRequired:
                case LauncherPrimaryAction.UpdateAndStart:
                case LauncherPrimaryAction.ContinueSetup:
                case LauncherPrimaryAction.StartOverSetup:
                case LauncherPrimaryAction.Prepare:
                case LauncherPrimaryAction.Continue:
                    DialogResult = DialogResult.OK;
                    break;
            }
        };

        Button? secondary = null;
        if (_model.SecondaryAction is not null)
        {
            secondary = CreateActionButton(
                _model.SecondaryActionLabel!,
                Theme.SurfaceLight,
                Theme.TextPrimary,
                true);
            secondary.Name = "SecondaryActionButton";
            secondary.Width = 204;
            secondary.Height = 34;
            secondary.Click += (_, _) =>
            {
                SelectedPrimaryAction = _model.SecondaryAction!.Value;
                DialogResult = DialogResult.OK;
            };
        }

        var close = CreateActionButton(localizer.T("launcher.close"), Theme.Surface, Theme.TextPrimary, true);
        close.Name = "CloseButton";
        close.Width = 128;
        close.Height = 34;
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;

        footer.Controls.Add(close);
        footer.Controls.Add(refresh);
        if (secondary is not null)
        {
            footer.Controls.Add(secondary);
        }
        footer.Controls.Add(primary);

        _footerButtons.Add(close);
        _footerButtons.Add(refresh);
        _footerButtons.Add(primary);
        if (secondary is not null)
        {
            _footerButtons.Add(secondary);
        }

        if (_model.HasActionLink)
        {
            var openAll = CreateActionButton(localizer.T("launcher.open_links"), Theme.Stroke, Theme.TextPrimary, true);
            openAll.Name = "OpenRequiredLinksButton";
            openAll.Width = 204;
            openAll.Height = 34;
            openAll.Click += (_, _) => OpenAll();
            footer.Controls.Add(openAll);
            _footerButtons.Add(openAll);
        }

        return footer;
    }

    private static Button CreateActionButton(string text, Color backColor, Color foreColor, bool enabled)
    {
        var messageFont = SystemFonts.MessageBoxFont ?? new Font(FontFamily.GenericSansSerif, 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        var btn = new Button
        {
            Text = text,
            BackColor = backColor,
            ForeColor = foreColor,
            Font = new Font(messageFont.FontFamily, 9.5f, FontStyle.Bold, GraphicsUnit.Point),
            FlatStyle = FlatStyle.Flat,
            Height = 34,
            Width = 204,
            Margin = new Padding(0, 12, 10, 0),
            Padding = new Padding(0),
            Enabled = enabled
        };
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = Theme.CardBorder;
        btn.FlatAppearance.MouseOverBackColor = ControlPaint.Dark(backColor, 0.15f);
        btn.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(backColor, 0.25f);
        return btn;
    }

    private static Font CreateThemeFont(float emSize, FontStyle style)
    {
        var fallback = SystemFonts.MessageBoxFont ?? new Font(FontFamily.GenericSansSerif, emSize, style, GraphicsUnit.Point);
        return new Font(fallback.FontFamily, emSize, style, GraphicsUnit.Point);
    }

    private static void OpenUrl(string url)
    {
        var info = new ProcessStartInfo(url)
        {
            UseShellExecute = true
        };

        _ = Process.Start(info);
    }

    private void OpenAll()
    {
        foreach (var requirement in _model.Requirements)
        {
            if (!string.IsNullOrWhiteSpace(requirement.ActionUrl))
            {
                OpenUrl(requirement.ActionUrl);
            }
        }
    }

    private static void PaintGlassBackground(Graphics graphics, Rectangle bounds)
    {
        using var background = new LinearGradientBrush(
            bounds,
            Theme.BackgroundTop,
            Theme.BackgroundBottom,
            LinearGradientMode.Vertical);
        graphics.FillRectangle(background, bounds);

        using var leftGlow = new PathGradientBrush(GetSoftBubbleRect(new Point(90, 320), 260, 190))
        {
            CenterColor = Theme.GlowPrimary,
            SurroundColors = [Color.Transparent]
        };
        graphics.FillEllipse(leftGlow, new Rectangle(60, 360, 280, 180));

        using var rightGlow = new PathGradientBrush(GetSoftBubbleRect(new Point(620, -170), 300, 240))
        {
            CenterColor = Theme.GlowSoft,
            SurroundColors = [Color.Transparent]
        };
        graphics.FillEllipse(rightGlow, new Rectangle(580, -140, 300, 240));
    }

    private static GraphicsPath GetSoftBubbleRect(Point center, int width, int height)
    {
        var path = new GraphicsPath();
        path.AddEllipse(new Rectangle(center.X - width / 2, center.Y - height / 2, width, height));
        return path;
    }

    private int ResolveContentWidth(bool reserveVerticalScrollbar = false)
    {
        var scrollbarWidth = reserveVerticalScrollbar ? SystemInformation.VerticalScrollBarWidth : 0;
        return Math.Max(WindowCardMinWidth, Math.Min(WindowContentMaxWidth, Math.Max(1, ClientSize.Width - WindowPadding * 2 - 8 - scrollbarWidth)));
    }

    private void TickAnimations()
    {
        _frame++;
        _fadeProgress = Math.Min(1f, _fadeProgress + 0.08f);
        Opacity = _fadeProgress;

        var cardsAnimating = false;
        foreach (var card in _cards)
        {
            if (_frame < card.RevealDelay)
            {
                cardsAnimating = true;
                continue;
            }

            if (card.RevealProgress < 1f)
            {
                card.RevealProgress = Math.Min(1f, card.RevealProgress + 0.07f);
                card.Invalidate();
                cardsAnimating = true;
            }
        }

        if (_fadeProgress >= 1f && !cardsAnimating)
        {
            _animationTimer.Stop();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animationTimer.Dispose();
            _inlineSpinnerTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    public static class Theme
    {
        public static readonly Color Background = Color.FromArgb(0x08, 0x0f, 0x1e);
        public static readonly Color BackgroundTop = Color.FromArgb(0x0a, 0x12, 0x24);
        public static readonly Color BackgroundBottom = Color.FromArgb(0x0f, 0x1a, 0x31);
        public static readonly Color Surface = Color.FromArgb(0x1b, 0x1e, 0x2b);
        public static readonly Color SurfaceLight = Color.FromArgb(0x28, 0x2b, 0x3b);
        public static readonly Color TextPrimary = Color.FromArgb(0xf5, 0xef, 0xff);
        public static readonly Color TextSecondary = Color.FromArgb(0xbb, 0xbd, 0xd0);
        public static readonly Color TextMuted = Color.FromArgb(0x98, 0x9c, 0xb4);
        public static readonly Color TextOnAccent = Color.FromArgb(0xff, 0xff, 0xff);
        public static readonly Color Accent = Color.FromArgb(0x70, 0x3c, 0xe6);
        public static readonly Color AccentSoft = Color.FromArgb(0xa0, 0x7c, 0xff);
        public static readonly Color Stroke = Color.FromArgb(0x56, 0x12, 0x50);
        public static readonly Color CardTop = Color.FromArgb(0x1e, 0x2a, 0x50);
        public static readonly Color CardBottom = Color.FromArgb(0x16, 0x23, 0x46);
        public static readonly Color CardLine = Color.FromArgb(0x71, 0x52, 0xa8);
        public static readonly Color GlowPrimary = Color.FromArgb(0x12, 0x43, 0x8a, 0xbf);
        public static readonly Color GlowSoft = Color.FromArgb(0x14, 0x79, 0x4a, 0xb8);
        public static readonly Color CardBorder = Color.FromArgb(0x48, 0x4a, 0x65);

        public static Color StatusBadge(PreflightRequirementState state)
            => state switch
            {
                PreflightRequirementState.Ok => Accent,
                PreflightRequirementState.Warning => Color.FromArgb(0x9e, 0x7f, 0xcd),
                PreflightRequirementState.Blocker => Color.FromArgb(0x70, 0x3f, 0xa9),
                _ => Accent
            };
    }
}

public sealed record LauncherInlineActionResult(bool Success, string Title, string Detail);

public sealed class GlassPanel : Panel
{
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(0f)]
    public float RevealProgress { get; set; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    [DefaultValue(0)]
    public int RevealDelay { get; set; }

    public GlassPanel()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);
        DoubleBuffered = true;
        BackColor = Color.Transparent;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);

        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0)
        {
            return;
        }

        using var path = RoundedRectPath(r, 14f);
        using var bg = new LinearGradientBrush(
            r,
            LauncherWindow.Theme.CardTop,
            LauncherWindow.Theme.CardBottom,
            LinearGradientMode.Vertical);
        e.Graphics.FillPath(bg, path);

        using var lineBrush = new LinearGradientBrush(
            new Rectangle(12, 16, Math.Max(1, Width - 24), 12),
            Color.FromArgb(60, LauncherWindow.Theme.CardLine),
            Color.Transparent,
            LinearGradientMode.Vertical);
        e.Graphics.FillRectangle(lineBrush, new Rectangle(12, 16, Math.Max(1, Width - 24), 12));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        if (r.Width <= 0 || r.Height <= 0)
        {
            return;
        }

        using var path = RoundedRectPath(r, 14f);
        using var border = new Pen(Color.FromArgb(120, LauncherWindow.Theme.CardBorder), 1f);
        e.Graphics.DrawPath(border, path);

        base.OnPaint(e);
    }

    private static GraphicsPath RoundedRectPath(Rectangle rectangle, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
