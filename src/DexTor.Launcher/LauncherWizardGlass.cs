using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using static DexTor.Launcher.WizardDesign;

namespace DexTor.Launcher;

/// <summary>The icons of the design: Segoe Fluent Icons where it has the same shape, drawn where it has not.</summary>
internal enum WizardIcon
{
    None, Monitor, Download, Usb, ExternalDrive, Shield, ShieldCheck, Clock, Windows, Memory, Drive, Layers, Hash,
    Refresh, Play, Restart, Info, Warning, Alert, ChevronDown, Close, Check
}

internal sealed partial class LauncherWizardWindow
{
    internal static void PaintGlassParent(Control control, PaintEventArgs e, Action<Control, PaintEventArgs> paint)
    {
        e.Graphics.SetClip(control.ClientRectangle, CombineMode.Intersect);
        var state = e.Graphics.Save();
        e.Graphics.TranslateTransform(-control.Left, -control.Top);
        if (control.Parent is not null)
        {
            using var parentArgs = new PaintEventArgs(e.Graphics, control.Parent.ClientRectangle);
            paint(control.Parent, parentArgs);
        }
        e.Graphics.Restore(state);
    }

    // ---------------------------------------------------------------- icons

    private static readonly string IconFamily = PickIconFamily();

    private static string PickIconFamily()
    {
        using var probe = new Font("Segoe Fluent Icons", 10f);
        return string.Equals(probe.Name, "Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase) ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
    }

    private static string? Glyph(WizardIcon icon) => icon switch
    {
        WizardIcon.Monitor => "",
        WizardIcon.Download => "",
        WizardIcon.Usb => "",
        WizardIcon.Clock => "",
        WizardIcon.Layers => "",
        WizardIcon.Refresh => "",
        WizardIcon.Play => "",
        WizardIcon.Restart => "",
        WizardIcon.Info => "",
        WizardIcon.Warning => "",
        WizardIcon.Alert => "",
        WizardIcon.ChevronDown => "",
        WizardIcon.Close => "",
        WizardIcon.Check => "",
        WizardIcon.Shield => "",
        _ => null
    };

    /// <summary>Draws <paramref name="icon"/> centred in <paramref name="box"/>, <paramref name="size"/> design pixels tall.</summary>
    internal static void DrawIcon(Graphics g, WizardIcon icon, RectangleF box, Color colour, float size = 16)
    {
        if (icon == WizardIcon.None) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Glyph(icon) is { } glyph)
        {
            using var font = new Font(IconFamily, Sf(size) * 72f / g.DpiY, FontStyle.Regular, GraphicsUnit.Point);
            TextRenderer.DrawText(g, glyph, font, Rectangle.Round(box), colour,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return;
        }

        // Drawn on a 20-unit grid around the centre, with the font icons' stroke weight.
        var unit = Sf(size) / 20f;
        var cx = box.Left + box.Width / 2;
        var cy = box.Top + box.Height / 2;
        PointF P(float x, float y) => new(cx + x * unit, cy + y * unit);
        using var pen = new Pen(colour, Math.Max(1f, 1.3f * unit)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (icon)
        {
            case WizardIcon.Windows:
            {
                // Four panes in perspective, as the design's Windows mark.
                PointF[] outline = [P(-8.5f, -6.4f), P(8.5f, -9.6f), P(8.5f, 9.6f), P(-8.5f, 6.4f)];
                g.DrawPolygon(pen, outline);
                g.DrawLine(pen, P(-1.2f, -7.8f), P(-1.2f, 7.8f));
                g.DrawLine(pen, P(-8.5f, 0), P(8.5f, 0));
                break;
            }
            case WizardIcon.Memory:
            {
                // A memory module: the board, three chips as short bars, pins along the bottom.
                using (var board = Rounded(new RectangleF(P(-9, -5).X, P(-9, -5).Y, 18 * unit, 9 * unit), 1.4f * unit)) g.DrawPath(pen, board);
                foreach (var x in new[] { -3.5f, 0f, 3.5f }) g.DrawLine(pen, P(x, -2.2f), P(x, 1.2f));
                foreach (var x in new[] { -6.5f, -3.2f, 0f, 3.2f, 6.5f }) g.DrawLine(pen, P(x, 4), P(x, 6.6f));
                break;
            }
            case WizardIcon.Drive:
            case WizardIcon.ExternalDrive:
            {
                // A drive seen from the front: a sloping top and a box with a light.
                using (var body = Rounded(new RectangleF(P(-9.5f, 0.5f).X, P(-9.5f, 0.5f).Y, 19 * unit, 8 * unit), 1.6f * unit)) g.DrawPath(pen, body);
                g.DrawLines(pen, [P(-9.5f, 0.9f), P(-5.4f, -8), P(5.4f, -8), P(9.5f, 0.9f)]);
                using var light = new SolidBrush(colour);
                g.FillEllipse(light, cx + 5f * unit, cy + 3.4f * unit, 2f * unit, 2f * unit);
                break;
            }
            case WizardIcon.ShieldCheck:
            {
                using var shield = new GraphicsPath();
                shield.AddLine(P(0, -8.4f), P(7, -5.6f));
                shield.AddBezier(P(7, -5.6f), P(7, 1.8f), P(4.6f, 5.8f), P(0, 8.4f));
                shield.AddBezier(P(0, 8.4f), P(-4.6f, 5.8f), P(-7, 1.8f), P(-7, -5.6f));
                shield.CloseFigure();
                g.DrawPath(pen, shield);
                g.DrawLines(pen, [P(-3.2f, 0), P(-0.9f, 2.4f), P(3.4f, -2)]);
                break;
            }
            case WizardIcon.Hash:
                g.DrawLine(pen, P(-2.6f, -8), P(-3.6f, 8));
                g.DrawLine(pen, P(3.6f, -8), P(2.6f, 8));
                g.DrawLine(pen, P(-7.6f, -3), P(7.6f, -3));
                g.DrawLine(pen, P(-7.6f, 3), P(7.6f, 3));
                break;
        }
    }

    // ---------------------------------------------------------------- surfaces

    /// <summary>The window surface: one flat colour everywhere, and the go buttons' halo.</summary>
    private sealed class GlassBackdrop : Panel, IGlowHost
    {
        public GlassBackdrop()
        {
            DoubleBuffered = true;
            BackColor = WizardDesign.Window;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using var background = new SolidBrush(WizardDesign.Window);
            e.Graphics.FillRectangle(background, ClientRectangle);
            PaintGoGlows(this, e.Graphics);
        }
    }

    /// <summary>
    /// A surface that paints the halo of the go buttons on it. The halo reaches well past the
    /// button, over its neighbours and the panels around it; painted by the button itself it
    /// was cut off square at the button's edges - grey corners beside it. Painted here, under
    /// everything, it shows through every transparent layer the way the rest of the surface does.
    /// </summary>
    private interface IGlowHost;

    private static void PaintGoGlows(Control host, Graphics g)
    {
        foreach (var button in GoButtons(host))
        {
            if (button.GlowBounds(host) is { } bounds) PaintGoGlow(g, bounds, button.Look);
        }
    }

    private static IEnumerable<GlassButton> GoButtons(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (!child.Visible) continue;
            if (child is GlassButton { Kind: ButtonKind.Go, Enabled: true } button) yield return button;
            foreach (var nested in GoButtons(child)) yield return nested;
        }
    }

    /// <summary>
    /// A card: #16151E with the #2C2A3A hairline and r12, 16 px in. It grows with what it holds.
    /// </summary>
    private sealed class WizardInfoPanel : FlowLayoutPanel
    {
        public WizardInfoPanel(int width)
        {
            BackColor = Color.Transparent;
            DoubleBuffered = true;
            // The surface is drawn to the card's height, so a card that grows - the creating
            // page's chart appearing under the stages - repaints whole. Otherwise Windows paints
            // only the strip that was added and the old bottom edge stays across the chart.
            SetStyle(ControlStyles.ResizeRedraw, true);
            FlowDirection = FlowDirection.TopDown;
            WrapContents = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            MinimumSize = new Size(width, 0);
            MaximumSize = new Size(width, 0);
            Width = width;
            Padding = S(16, 16, 16, 16);
            Margin = Padding.Empty;
            Name = "WizardInfoPanel";
        }

        /// <summary>Inner width: what a row inside it may take.</summary>
        internal int Inner => MinimumSize.Width - Padding.Horizontal;

        /// <summary>The card colour; the facts box of a dialog takes the row colour instead.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color Fill { get; set; } = WizardDesign.Card;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color Border { get; set; } = WizardDesign.CardBorder;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal float Radius { get; set; } = 12;

        public override Size GetPreferredSize(Size proposedSize)
        {
            var size = base.GetPreferredSize(proposedSize);
            var last = Controls.Cast<Control>().LastOrDefault(control => control.Visible);
            return last is null ? size : new Size(size.Width, size.Height - last.Margin.Bottom);
        }

        /// <summary>A card with no surface of its own - a group of rows straight on the page.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Bare { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            if (!Bare) PaintSurface(e.Graphics, ClientRectangle, Radius, Fill, Border);
        }
    }

    /// <summary>The small caps line that names a card or a group inside it.</summary>
    private static WizardText CreateCaption(string text, string name = "WizardCaption") => new(12, 600, 16, WizardDesign.TextSecondary)
    {
        Name = name, Text = text, Caps = true, LetterSpacing = 0.06f, Margin = S(0, 0, 0, 8)
    };

    /// <summary>
    /// A rounded tile with an icon - 40 x 40 (32 in a row with no detail line), r8, #272436 -
    /// and an optional green "done" badge over its corner.
    /// </summary>
    private sealed class IconTile : Control
    {
        private float _side = 40;
        private bool _badge;

        public IconTile(WizardIcon icon)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Icon = icon;
            Size = S(40, 40);
            TabStop = false;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal WizardIcon Icon { get; set; }

        /// <summary>The tile's side in design pixels.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal float Side
        {
            get => _side;
            set { _side = value; UpdateSize(); }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool DoneBadge
        {
            get => _badge;
            set { _badge = value; UpdateSize(); }
        }

        /// <summary>The badge reaches 4 px past the tile, so the control is that much larger.</summary>
        private void UpdateSize()
        {
            Size = S(_side + (_badge ? 4 : 0), _side + (_badge ? 4 : 0));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var tile = new Rectangle(0, 0, S(_side), S(_side));
            PaintSurface(e.Graphics, tile, 8, Tile, Color.Transparent, highlight: false);
            DrawIcon(e.Graphics, Icon, tile, WizardDesign.TextSecondary);
            if (!_badge) return;
            // As in the design: a 16 px green disc over the tile's corner, ringed in the row colour.
            var disc = new RectangleF(Sf(_side - 13), Sf(_side - 14), Sf(16), Sf(16));
            using (var ring = new SolidBrush(WizardDesign.Row)) e.Graphics.FillEllipse(ring, RectangleF.Inflate(disc, Sf(2), Sf(2)));
            PaintMedallion(e.Graphics, disc, Tone.Success, glow: 3);
        }
    }

    /// <summary>
    /// A row of a card (a "plate"): #1D1B28, the #322F42 hairline, r10. An icon tile, a title
    /// and a detail line, and an optional status at the end. Content is centred vertically.
    /// </summary>
    private sealed class WizardRow : Panel
    {
        private readonly IconTile? _tile;
        private Control? _trailing;

        public WizardRow(int width, WizardIcon icon, string title, string? detail)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Margin = S(0, 0, 0, 8);
            TabStop = false;
            if (icon != WizardIcon.None)
            {
                _tile = new IconTile(icon);
                Controls.Add(_tile);
            }

            TitleText = new WizardText(16, 600, 20, WizardDesign.Text) { Name = "WizardRowTitle", Text = title };
            Controls.Add(TitleText);
            DetailText = new WizardText(14, 400, 20, WizardDesign.TextSecondary) { Name = "WizardRowDetail", Text = detail ?? string.Empty };
            Controls.Add(DetailText);
            Text = string.IsNullOrEmpty(detail) ? title : title + Environment.NewLine + detail;
            TitleText.TextChanged += (_, _) => Relayout();
            DetailText.TextChanged += (_, _) => Relayout();
            _ready = true;
            Width = width;
            Relayout();
        }

        private readonly bool _ready;

        internal WizardText TitleText { get; }

        internal WizardText DetailText { get; }

        internal IconTile? Tile => _tile;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Color Border { get; set; } = WizardDesign.RowBorder;

        /// <summary>
        /// The intro's rows are set tighter than the others: 52 px with a one-line detail
        /// instead of 60.
        /// </summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Dense { get; set; }

        internal void SetTrailing(Control? trailing)
        {
            if (_trailing is not null)
            {
                Controls.Remove(_trailing);
                _trailing.Dispose();
            }

            _trailing = trailing;
            if (trailing is not null) Controls.Add(trailing);
            Relayout();
        }

        internal Control? Trailing => _trailing;

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Relayout();
        }

        private bool _laying;

        internal void Relayout()
        {
            if (_laying || !_ready) return;
            _laying = true;
            try
            {
                var hasDetail = DetailText.Text.Length > 0;
                // A row with no detail line is 44 px and its tile 32; otherwise the tile is 40.
                if (_tile is not null) _tile.Side = hasDetail ? 40 : 32;
                var tileSide = hasDetail ? 40 : 32;
                var left = _tile is null ? S(16) : S(16 + tileSide + 16);
                var right = S(16);
                if (_trailing is not null)
                {
                    _trailing.Size = _trailing.GetPreferredSize(Size.Empty);
                    right += _trailing.Width + S(16);
                }

                var wrap = Math.Max(S(40), Width - left - right);
                TitleText.WrapWidth = 0;
                TitleText.MaximumSize = new Size(wrap, 0);
                DetailText.WrapWidth = wrap;
                DetailText.Visible = hasDetail;
                var block = TitleText.Height + (hasDetail ? S(4) + DetailText.Height : 0);
                // 44 px with a title only; with a detail 8 px above and below the text - the
                // intro's rows 4.
                Height = !hasDetail ? S(44) : block + S(Dense && DetailText.Lines().Count <= 1 ? 4 : 8) * 2;
                var top = (Height - block) / 2;
                TitleText.Location = new Point(left, top);
                DetailText.Location = new Point(left, top + TitleText.Height + S(4));
                if (_tile is not null) _tile.Location = new Point(S(16), (Height - S(tileSide)) / 2);
                if (_trailing is not null) _trailing.Location = new Point(Width - S(16) - _trailing.Width, (Height - _trailing.Height) / 2);
            }
            finally
            {
                _laying = false;
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            PaintSurface(e.Graphics, ClientRectangle, 10, WizardDesign.Row, Border);
        }
    }

    /// <summary>What a row found: a medallion and the value, or a check still running or waiting.</summary>
    private sealed class RowStatus : Control
    {
        internal enum Kind { Ok, Warning, Error, Checking, Pending }

        private readonly Kind _kind;

        public RowStatus(string text, Kind kind)
        {
            _kind = kind;
            Text = text;
            Font = WizardFonts.Get(14, 600);
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            TabStop = false;
            Size = GetPreferredSize(Size.Empty);
        }

        internal Kind State => _kind;

        public override Size GetPreferredSize(Size proposedSize) => new(
            S(20 + 8) + DesignText.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 1, S(28));

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            var disc = new RectangleF(Sf(0), (Height - Sf(20)) / 2f, Sf(20), Sf(20));
            switch (_kind)
            {
                case Kind.Ok: PaintMedallion(g, disc, Tone.Success, 5); break;
                case Kind.Warning: PaintMedallion(g, disc, Tone.Warning, 5); break;
                case Kind.Error: PaintMedallion(g, disc, Tone.Error, 5); break;
                case Kind.Checking: PaintChecking(g, disc); break;
                default: PaintPending(g, disc); break;
            }

            var colour = _kind switch
            {
                Kind.Warning => WizardDesign.WarningText,
                Kind.Error => WizardDesign.Error,
                Kind.Pending => WizardDesign.TextDisabled,
                _ => WizardDesign.Text
            };
            DesignText.DrawText(g, Text, Font, new Rectangle(S(28), 0, Width - S(28), Height), colour,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>
    /// A highlighted block: orange for "keep in mind", green for "all good", with an icon (or a
    /// medallion) in a 40 px column, the text, and - optionally - a quiet action on the right.
    /// </summary>
    private sealed class GlassNotice : Control
    {
        public GlassNotice(int width)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Width = width;
            Font = WizardFonts.Get(14, 500);
            Margin = S(0, 4, 0, 0);
            TabStop = false;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Tone Kind { get; set; } = Tone.Warning;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal WizardIcon Icon { get; set; } = WizardIcon.Warning;

        /// <summary>A quiet action at the end of the block ("How to enable"); clicking it raises <see cref="ActionClicked"/>.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string? Action { get; set; }

        internal event EventHandler? ActionClicked;

        /// <summary>The block's height with one line of text; it grows when the text wraps.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal float MinimumLineHeight { get; set; } = 44;

        private const TextFormatFlags WrapFlags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;

        private int TextLeft => Kind == Tone.Success ? S(67) : Icon == WizardIcon.None ? S(16) : S(72);

        /// <summary>Space above and below the text when it wraps; one line is centred in <see cref="MinimumLineHeight"/>.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal float VerticalPadding { get; set; } = 11;

        private int ActionWidth => Action is null ? 0 : DesignText.MeasureText(Action, ActionFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(16);

        private Font ActionFont => _actionFont ??= WizardFonts.Get(14, 600);
        private Font? _actionFont;

        private int TextWidth => Math.Max(S(40), Width - TextLeft - S(16) - ActionWidth);

        private WizardText TextLayout() => new(14, 500, 20, Color.Empty) { Text = Text, WrapWidth = TextWidth };

        public override Size GetPreferredSize(Size proposedSize)
        {
            using var probe = TextLayout();
            var lines = probe.Lines().Count;
            return new Size(Width, Math.Max(S(MinimumLineHeight), lines * S(20) + S(VerticalPadding) * 2));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Height = GetPreferredSize(Size.Empty).Height;
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            var wanted = GetPreferredSize(Size.Empty).Height;
            if (Height != wanted) Height = wanted;
        }

        private Rectangle ActionBounds => new(Width - S(16) - ActionWidth + S(16), 0, ActionWidth - S(16), Height);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = Action is not null && ActionBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (Action is not null && ActionBounds.Contains(e.Location)) ActionClicked?.Invoke(this, EventArgs.Empty);
        }

        internal void PerformAction() => ActionClicked?.Invoke(this, EventArgs.Empty);

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            PaintToneBlock(g, ClientRectangle, Kind);
            var (iconColour, textColour) = Kind switch
            {
                Tone.Success => (WizardDesign.Green, WizardDesign.GreenLight),
                Tone.Error => (WizardDesign.Error, WizardDesign.Error),
                _ => (WizardDesign.Warning, WizardDesign.WarningText)
            };
            if (Kind == Tone.Success)
            {
                PaintMedallion(g, new RectangleF(Sf(23), (Height - Sf(24)) / 2f, Sf(24), Sf(24)), Tone.Success, 6);
            }
            else
            {
                DrawIcon(g, Icon, new RectangleF(Sf(16), 0, Sf(40), Height), iconColour, 16);
            }

            using var layout = TextLayout();
            var lines = layout.Lines();
            var top = (Height - lines.Count * S(20)) / 2;
            var fontHeight = DesignText.MeasureText("Ag", Font, Size.Empty, TextFormatFlags.NoPadding).Height;
            for (var i = 0; i < lines.Count; i++)
            {
                DesignText.DrawText(g, lines[i], Font, new Point(TextLeft, top + i * S(20) + (S(20) - fontHeight) / 2), textColour,
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
            }

            if (Action is not null)
            {
                DesignText.DrawText(g, Action, ActionFont, ActionBounds, WizardDesign.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _actionFont?.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// A red block: what failed, in a 16 px title, and what to do, under it - with the lit red
    /// tile of the design on the left.
    /// </summary>
    private sealed class ErrorBlock : Panel
    {
        public ErrorBlock(int width, string title, string detail)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Width = width;
            Margin = S(0, 0, 0, 8);
            TitleText = new WizardText(16, 600, 20, WizardDesign.Text) { Name = "WizardErrorTitle", Text = title };
            DetailText = new WizardText(14, 400, 20, WizardDesign.TextSecondary) { Name = "WizardErrorDetail", Text = detail, WrapWidth = width - S(72 + 16) };
            Controls.Add(TitleText);
            Controls.Add(DetailText);
            Text = title + Environment.NewLine + detail;
            TitleText.TextChanged += (_, _) => Relayout();
            DetailText.TextChanged += (_, _) => Relayout();
            Relayout();
        }

        internal WizardText TitleText { get; }

        internal WizardText DetailText { get; }

        private void Relayout()
        {
            DetailText.Visible = DetailText.Text.Length > 0;
            var block = TitleText.Height + (DetailText.Visible ? S(4) + DetailText.Height : 0);
            Height = Math.Max(S(56), block + S(8) * 2);
            var top = (Height - block) / 2;
            TitleText.Location = new Point(S(72), top);
            DetailText.Location = new Point(S(72), top + TitleText.Height + S(4));
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            PaintToneBlock(g, ClientRectangle, Tone.Error);
            var tile = new RectangleF(Sf(16), (Height - Sf(40)) / 2f, Sf(40), Sf(40));
            PaintLitTile(g, tile, WizardDesign.ErrorTop, WizardDesign.ErrorBottom, WizardDesign.Error);
            DrawIcon(g, WizardIcon.Alert, tile, Color.White, 16);
        }
    }

    /// <summary>"▸ Details": opens and closes the technical details under a block.</summary>
    private sealed class DetailsToggle : Control
    {
        private bool _open;

        public DetailsToggle(string text)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Text = text;
            Font = WizardFonts.Get(14, 400);
            Cursor = Cursors.Hand;
            Size = new Size(DesignText.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding).Width + S(20), S(24));
            Margin = S(0, 0, 0, 0);
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Control? Target { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Open
        {
            get => _open;
            set
            {
                _open = value;
                if (Target is not null) Target.Visible = value;
                Invalidate();
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Open = !Open;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var cy = Height / 2f;
            using var brush = new SolidBrush(WizardDesign.Link);
            PointF[] arrow = _open
                ? [new(Sf(1), cy - Sf(3)), new(Sf(10), cy - Sf(3)), new(Sf(5.5f), cy + Sf(3.5f))]
                : [new(Sf(2), cy - Sf(5)), new(Sf(9), cy), new(Sf(2), cy + Sf(5))];
            g.FillPolygon(brush, arrow);
            DesignText.DrawText(g, Text, Font, new Rectangle(S(16), 0, Width - S(16), Height), WizardDesign.Link,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>The technical details: names on the left, monospaced values on the right.</summary>
    private sealed class DetailsBox : Control
    {
        private (string Name, string Value)[] _lines = [];

        public DetailsBox(int width)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Width = width;
            Margin = S(0, 8, 0, 0);
            TabStop = false;
        }

        internal void SetLines((string Name, string Value)[] lines)
        {
            _lines = lines;
            Text = string.Join(Environment.NewLine, lines.Select(line => $"{line.Name}: {line.Value}"));
            Height = S(7) * 2 + lines.Length * S(22);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            PaintSurface(g, ClientRectangle, 10, WizardDesign.Row, WizardDesign.RowBorder);
            using var name = WizardFonts.Get(14, 400);
            using var value = new Font("Consolas", Sf(13.5f) * 72f / g.DpiY, FontStyle.Bold, GraphicsUnit.Point);
            for (var i = 0; i < _lines.Length; i++)
            {
                var row = new Rectangle(S(16), S(7) + i * S(22), Width - S(32), S(22));
                DesignText.DrawText(g, _lines[i].Name, name, row with { Width = S(128) }, WizardDesign.TextSecondary,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, _lines[i].Value, value, row with { X = S(149), Width = Width - S(149 + 16) }, WizardDesign.Text,
                    TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }
    }

    /// <summary>"You will need": the caps caption, then figures with icons, split by hairlines - all on one line.</summary>
    private sealed class FactStrip : Control
    {
        private readonly (WizardIcon Icon, string Value, string Caption)[] _facts;
        private readonly string _title;

        public FactStrip(string title, (WizardIcon Icon, string Value, string Caption)[] facts)
        {
            _title = title;
            _facts = facts;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Height = S(20);
            TabStop = false;
            Text = title + ": " + string.Join(", ", facts.Select(fact => $"{fact.Value} {fact.Caption}"));
            AccessibleName = Text;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            using var caption = WizardFonts.Get(12, 600);
            using var value = WizardFonts.Get(14, 600);
            using var detail = WizardFonts.Get(14, 400);
            using var divider = new Pen(WizardDesign.Divider);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            var capsHeight = DesignText.MeasureText("A", caption, Size.Empty, flags).Height;
            var bodyHeight = DesignText.MeasureText("A", value, Size.Empty, flags).Height;
            var title = _title.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
            var x = DesignText.Draw(g, title, caption, WizardDesign.TextSecondary, 0, (Height - capsHeight) / 2, 0.06f) + S(18);
            for (var i = 0; i < _facts.Length; i++)
            {
                if (i > 0)
                {
                    x += S(16);
                    g.DrawLine(divider, x, S(2), x, Height - S(2));
                    x += S(18);
                }

                DrawIcon(g, _facts[i].Icon, new RectangleF(x, 0, Sf(16), Height), WizardDesign.TextSecondary, 16);
                x += S(16 + 8);
                DesignText.DrawText(g, _facts[i].Value, value, new Point((int)x, (Height - bodyHeight) / 2), WizardDesign.Text, flags);
                x += DesignText.MeasureText(g, _facts[i].Value + " ", value, Size.Empty, flags).Width;
                DesignText.DrawText(g, _facts[i].Caption, detail, new Point((int)x, (Height - bodyHeight) / 2), WizardDesign.TextSecondary, flags);
                x += DesignText.MeasureText(g, _facts[i].Caption, detail, Size.Empty, flags).Width;
            }
        }
    }

    /// <summary>Label-over-value columns: disk, model, size, free space.</summary>
    private sealed class FactGrid : Control
    {
        private (string Caption, string Value)[] _facts = [];

        public FactGrid()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Height = S(44);
            TabStop = false;
        }

        internal IReadOnlyList<(string Caption, string Value)> Facts => _facts;

        internal void SetFacts((string Caption, string Value)[] facts)
        {
            _facts = facts;
            AccessibleName = string.Join(", ", facts.Select(fact => $"{fact.Caption}: {fact.Value}"));
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            if (_facts.Length == 0) return;
            using var captionFont = WizardFonts.Get(14, 400);
            using var valueFont = WizardFonts.Get(14, 600);
            var column = Width / _facts.Length;
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            for (var index = 0; index < _facts.Length; index++)
            {
                var bounds = new Rectangle(index * column, 0, column - S(16), S(20));
                DesignText.DrawText(e.Graphics, _facts[index].Caption, captionFont, bounds, WizardDesign.TextSecondary, flags);
                DesignText.DrawText(e.Graphics, _facts[index].Value, valueFont, bounds with { Y = S(24) }, WizardDesign.Text, flags);
            }
        }
    }

    /// <summary>A hairline across a row.</summary>
    private sealed class Divider : Control
    {
        public Divider()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Height = 1;
            TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            using var pen = new Pen(WizardDesign.RowBorder);
            e.Graphics.DrawLine(pen, 0, 0, Width, 0);
        }
    }

    // ---------------------------------------------------------------- buttons

    internal enum ButtonKind { Go, Secondary, Danger, DangerOutline, Outline }

    /// <summary>
    /// The design's buttons. Every one is 40 px, r8, 600-weight text; a disabled one keeps its
    /// text and icons and only changes colour.
    /// </summary>
    private sealed class GlassButton : Button
    {
        private bool _hover;
        private bool _pressed;

        public GlassButton()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = WizardFonts.Get(14, 600);
            Cursor = Cursors.Hand;
            Height = S(40);
            UseMnemonic = false;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal ButtonKind Kind { get; set; } = ButtonKind.Secondary;

        /// <summary>A mark after the label - the go arrow - in light green.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string? Trailing { get; set; }

        /// <summary>A mark before the label: "←", or an icon (<see cref="LeadingIcon"/>).</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal string? Leading { get; set; }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal WizardIcon LeadingIcon { get; set; }

        /// <summary>The waiting ring in a disabled "Starting…" button.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal bool Spinner { get; set; }

        /// <summary>The narrowest the button may be; it grows with its text beyond that.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int MinimumWidth { get; set; }

        internal bool Hovered => _hover;

        private Font ArrowFont => _arrowFont ??= WizardFonts.Get(14, 400);
        private Font? _arrowFont;

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Restyle(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Restyle(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Restyle(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Restyle(); base.OnMouseUp(e); }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Restyle();
            base.OnEnabledChanged(e);
        }

        protected override void OnVisibleChanged(EventArgs e) { InvalidateGlow(); base.OnVisibleChanged(e); }
        protected override void OnLocationChanged(EventArgs e) { InvalidateGlow(); base.OnLocationChanged(e); }
        protected override void OnSizeChanged(EventArgs e) { InvalidateGlow(); base.OnSizeChanged(e); }
        protected override void OnParentChanged(EventArgs e) { InvalidateGlow(); base.OnParentChanged(e); }

        internal ButtonLook Look => Visual;

        /// <summary>The surface that paints this button's halo - see <see cref="IGlowHost"/>.</summary>
        private Control? GlowHost
        {
            get
            {
                for (var parent = Parent; parent is not null; parent = parent.Parent)
                {
                    if (parent is IGlowHost) return parent;
                }

                return null;
            }
        }

        /// <summary>The button's rectangle in <paramref name="host"/>'s coordinates.</summary>
        internal RectangleF? GlowBounds(Control host)
        {
            var at = Location;
            for (var parent = Parent; parent != host; parent = parent.Parent)
            {
                if (parent is null) return null;
                at.Offset(parent.Location);
            }

            return new RectangleF(at.X, at.Y, Width, Height);
        }

        private Rectangle _glowArea;
        private Control? _glowAreaHost;

        private void Restyle()
        {
            Invalidate();
            InvalidateGlow();
        }

        /// <summary>Repaints where the halo was and where it is now, children included.</summary>
        private void InvalidateGlow()
        {
            if (_glowAreaHost is { IsDisposed: false } old && !_glowArea.IsEmpty) old.Invalidate(_glowArea, true);
            _glowArea = Rectangle.Empty;
            _glowAreaHost = null;
            if (Kind != ButtonKind.Go || GlowHost is not { } host || GlowBounds(host) is not { } bounds) return;
            var reach = S(WizardDesign.GoGlowReach) + 2;
            _glowArea = Rectangle.Inflate(Rectangle.Round(bounds), reach, reach);
            _glowAreaHost = host;
            host.Invalidate(_glowArea, true);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            FitWidth();
            Invalidate();
        }

        private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        private int ContentWidth()
        {
            var width = DesignText.MeasureText(Text, Font, Size.Empty, Flags).Width;
            if (!string.IsNullOrEmpty(Leading)) width += (int)Math.Ceiling(DesignText.Width(Leading + " ", ArrowFont));
            if (LeadingIcon != WizardIcon.None || Spinner) width += S(16 + 10);
            if (!string.IsNullOrEmpty(Trailing)) width += (int)Math.Ceiling(DesignText.Width(" " + Trailing, ArrowFont)) + S(2);
            return width;
        }

        internal void FitWidth()
        {
            if (MinimumWidth <= 0) return;
            Width = Math.Max(MinimumWidth, ContentWidth() + S(20) * 2);
        }

        private ButtonLook Visual => _pressed ? ButtonLook.Pressed : _hover ? ButtonLook.Hover : ButtonLook.Normal;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            PaintGlassParent(this, e, InvokePaintBackground);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var inset = Kind == ButtonKind.Go ? Sf(0) : 0;
            var bounds = new RectangleF(inset, inset, Width - 2 * inset, Height - 2 * inset);
            if (!Enabled)
            {
                PaintDisabledButton(g, bounds);
            }
            else
            {
                switch (Kind)
                {
                    case ButtonKind.Go: PaintGoButton(g, bounds, Visual, outerGlow: GlowHost is null); break;
                    case ButtonKind.Danger: PaintDangerButton(g, bounds, Visual); break;
                    case ButtonKind.DangerOutline: PaintDangerOutlineButton(g, bounds, Visual); break;
                    case ButtonKind.Outline:
                    {
                        using var path = Rounded(RectangleF.Inflate(bounds, -0.5f, -0.5f), Sf(8));
                        if (Visual != ButtonLook.Normal)
                        {
                            using var face = new SolidBrush(Visual == ButtonLook.Pressed ? WizardDesign.SecondaryPressed : WizardDesign.SecondaryHover);
                            g.FillPath(face, path);
                        }

                        using var pen = new Pen(WizardDesign.FieldBorder);
                        g.DrawPath(pen, path);
                        break;
                    }
                    default: PaintSecondaryButton(g, bounds, Visual); break;
                }
            }

            var text = !Enabled ? WizardDesign.TextDisabled
                : Kind == ButtonKind.DangerOutline ? WizardDesign.Error
                : WizardDesign.Text;
            var x = (Width - ContentWidth()) / 2f;
            var textHeight = DesignText.MeasureText("Ag", Font, Size.Empty, Flags).Height;
            var y = (Height - textHeight) / 2;
            if (Spinner)
            {
                PaintChecking(g, new RectangleF(x, (Height - Sf(16)) / 2f, Sf(16), Sf(16)));
                x += S(16 + 10);
            }
            else if (LeadingIcon != WizardIcon.None)
            {
                var iconColour = !Enabled ? WizardDesign.TextDisabled : LeadingIcon == WizardIcon.Play ? WizardDesign.GreenLight : text;
                DrawIcon(g, LeadingIcon, new RectangleF(x, 0, Sf(16), Height), iconColour, LeadingIcon == WizardIcon.Play ? 12 : 16);
                x += S(16 + 10);
            }

            if (!string.IsNullOrEmpty(Leading))
            {
                x = DesignText.Draw(g, Leading + " ", ArrowFont, text, x, y);
            }

            x = DesignText.Draw(g, Text, Font, text, x, y);
            if (!string.IsNullOrEmpty(Trailing))
            {
                var mark = Enabled && Kind == ButtonKind.Go ? WizardDesign.GreenLight : text;
                DesignText.Draw(g, " " + Trailing, ArrowFont, mark, x + Sf(2), y);
            }

            if (Focused && ShowFocusCues && Enabled)
            {
                using var focus = new Pen(Color.FromArgb(120, WizardDesign.Text)) { DashStyle = DashStyle.Dot };
                using var ring = Rounded(RectangleF.Inflate(bounds, -Sf(3), -Sf(3)), Sf(6));
                g.DrawPath(focus, ring);
            }
        }
    }

    // ---------------------------------------------------------------- header

    /// <summary>An outlined caps badge beside the wordmark ("BETA").</summary>
    private sealed class HeaderBadge : Label
    {
        public HeaderBadge()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            AutoSize = false;
            Height = S(23);
            BackColor = Color.Transparent;
            Font = WizardFonts.Get(12, 600);
        }

        private string Caps => Text.ToUpper(System.Globalization.CultureInfo.CurrentCulture);

        private int CapsWidth => (int)Math.Ceiling(DesignText.Width(Caps, Font, 0.06f));

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Width = CapsWidth + S(9) * 2;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e) => PaintGlassParent(this, e, InvokePaintBackground);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            PaintSurface(g, ClientRectangle, 6, Color.Transparent, WizardDesign.CardBorder, highlight: false);
            var height = DesignText.MeasureText(Caps, Font, Size.Empty, TextFormatFlags.NoPadding).Height;
            DesignText.Draw(g, Caps, Font, WizardDesign.TextSecondary, (Width - CapsWidth) / 2f, (Height - height) / 2, 0.06f);
        }
    }

    /// <summary>
    /// The stepper: six circles on one line, each with its name centred under it, joined by
    /// lines. Done steps and the lines between them are green and lit; the current one is
    /// green with a light ring; a failed one is red; steps to come are dark and quiet.
    /// </summary>
    private sealed class StepperView : Panel
    {
        private readonly List<StepCircle> _circles = [];
        private readonly List<WizardText> _labels = [];

        public StepperView()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
        }

        /// <summary>Circle centres: 19 px in from the content edges, evenly spaced.</summary>
        internal const float FirstCentre = 59, LastCentre = 901, CentreY = 26.4f, LabelTop = 52.2f;

        internal void Add(StepCircle circle, WizardText label)
        {
            _circles.Add(circle);
            _labels.Add(label);
            Controls.Add(circle);
            Controls.Add(label);
            label.SizeChanged += (_, _) => PlaceLabels();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            for (var i = 0; i < _circles.Count; i++)
            {
                var cx = Centre(i);
                _circles[i].Location = new Point((int)Math.Round(cx - _circles[i].Width / 2f), (int)Math.Round(Sf(CentreY) - _circles[i].Height / 2f));
            }

            PlaceLabels();
        }

        private float Centre(int index) => Sf(FirstCentre + (LastCentre - FirstCentre) * index / Math.Max(1, _circles.Count - 1));

        private void PlaceLabels()
        {
            for (var i = 0; i < _labels.Count; i++)
            {
                _labels[i].Location = new Point((int)Math.Round(Centre(i) - _labels[i].Width / 2f), S(LabelTop));
            }
        }

        internal bool LineDone(int index) => index + 1 < _circles.Count
            && _circles[index].State == StepCircle.Kind.Done
            && _circles[index + 1].State is StepCircle.Kind.Done or StepCircle.Kind.Current or StepCircle.Kind.Failed;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var window = new SolidBrush(WizardDesign.Window)) e.Graphics.FillRectangle(window, ClientRectangle);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var y = Sf(CentreY);
            var radius = Sf(16);
            for (var i = 0; i + 1 < _circles.Count; i++)
            {
                var from = Centre(i) + radius;
                var to = Centre(i + 1) - radius;
                if (LineDone(i))
                {
                    // A lit line that runs into the circles: its glow joins theirs.
                    var line = new RectangleF(Centre(i), y - Sf(1), Centre(i + 1) - Centre(i), Sf(2));
                    PaintOuterGlow(g, line, 1, WizardDesign.Green, 10, 0.18f);
                    using var pen = new Pen(WizardDesign.GreenBottom, Sf(1.6f));
                    g.DrawLine(pen, from - Sf(2), y, to + Sf(2), y);
                }
                else
                {
                    using var pen = new Pen(WizardDesign.CardBorder, Sf(1.6f));
                    g.DrawLine(pen, from, y, to, y);
                }
            }
        }
    }

    /// <summary>
    /// One circle of the stepper. The number is drawn as an outline centred on its own ink, so
    /// it never drifts with the font's box. A step that turns current grows its number in; a
    /// step that is done draws its tick. Nothing pulses: the design allows no waiting animation.
    /// </summary>
    private sealed class StepCircle : Label
    {
        internal enum Kind { Pending, Current, Done, Failed }

        private const double TickSeconds = 0.35;
        private const double GrowSeconds = 0.28;

        private Kind _kind;
        private readonly System.Diagnostics.Stopwatch _clock = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 };
        private GraphicsPath? _digit;
        private (string Text, float Size) _digitKey;

        public StepCircle()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            AutoSize = false;
            // Room for the glow around the 32 px circle.
            Size = S(56, 56);
            _timer.Tick += (_, _) =>
            {
                Invalidate();
                if (_clock.Elapsed.TotalSeconds > Math.Max(TickSeconds, GrowSeconds)) _timer.Stop();
            };
        }

        private static bool Animate => SystemInformation.UIEffectsEnabled;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Kind State
        {
            get => _kind;
            set
            {
                if (_kind == value) return;
                _kind = value;
                _clock.Restart();
                if (Animate && IsHandleCreated && value is Kind.Current or Kind.Done) _timer.Start();
                Invalidate();
                Parent?.Invalidate();
            }
        }

        private float Progress(double seconds)
        {
            if (!Animate || !_clock.IsRunning || !_timer.Enabled) return 1f;
            var t = Math.Clamp(_clock.Elapsed.TotalSeconds / seconds, 0, 1);
            return (float)(1 - Math.Pow(1 - t, 3));
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e) => PaintGlassParent(this, e, InvokePaintBackground);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var cx = Width / 2f;
            var cy = Height / 2f;
            RectangleF Disc(float diameter) => new(cx - Sf(diameter) / 2, cy - Sf(diameter) / 2, Sf(diameter), Sf(diameter));
            switch (_kind)
            {
                case Kind.Current:
                {
                    var fill = Disc(28);
                    PaintOuterGlow(g, Disc(36), Sf(18), WizardDesign.Green, 10, 0.30f, ellipse: true);
                    using (var gap = new SolidBrush(WizardDesign.Window)) g.FillEllipse(gap, Disc(35));
                    using (var ring = new Pen(Color.FromArgb(0xDF, 0xFF, 0xF0), Sf(2.4f))) g.DrawEllipse(ring, RectangleF.Inflate(Disc(37.6f), -Sf(1.2f), -Sf(1.2f)));
                    PaintLitDisc(g, fill, WizardDesign.GreenTop, WizardDesign.GreenBottom);
                    break;
                }
                case Kind.Done:
                    PaintOuterGlow(g, Disc(32), Sf(16), WizardDesign.Green, 10, 0.32f, ellipse: true);
                    PaintLitDisc(g, Disc(32), WizardDesign.GreenTop, WizardDesign.GreenBottom);
                    break;
                case Kind.Failed:
                    PaintOuterGlow(g, Disc(32), Sf(16), WizardDesign.Error, 10, 0.32f, ellipse: true);
                    PaintLitDisc(g, Disc(32), WizardDesign.ErrorTop, WizardDesign.ErrorBottom);
                    break;
                default:
                {
                    var disc = Disc(32);
                    using (var fill = new SolidBrush(Color.FromArgb(0x1C, 0x1C, 0x26))) g.FillEllipse(fill, disc);
                    using (var ring = new Pen(WizardDesign.CardBorder)) g.DrawEllipse(ring, RectangleF.Inflate(disc, -0.5f, -0.5f));
                    break;
                }
            }

            var unit = Sf(32) / 20f * 0.62f;
            if (_kind == Kind.Done)
            {
                DrawTick(g, Color.White, cx, cy + Sf(0.3f), unit, 2.6f, Progress(TickSeconds));
                return;
            }

            if (_kind == Kind.Failed)
            {
                using var mark = new Pen(Color.White, 2.4f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(mark, cx - 3.4f * unit, cy - 3.4f * unit, cx + 3.4f * unit, cy + 3.4f * unit);
                g.DrawLine(mark, cx + 3.4f * unit, cy - 3.4f * unit, cx - 3.4f * unit, cy + 3.4f * unit);
                return;
            }

            var digit = DigitPath(Text, Sf(14) * 1.0f);
            if (digit is null) return;
            var bounds = digit.GetBounds();
            var grow = _kind == Kind.Current ? 0.6f + 0.4f * Progress(GrowSeconds) : 1f;
            var state = g.Save();
            g.TranslateTransform(cx, cy);
            g.ScaleTransform(grow, grow);
            g.TranslateTransform(-(bounds.Left + bounds.Width / 2), -(bounds.Top + bounds.Height / 2));
            var colour = _kind == Kind.Current ? Color.White : WizardDesign.TextSecondary;
            using (var brush = new SolidBrush(colour)) g.FillPath(brush, digit);
            g.Restore(state);
        }

        private static void PaintLitDisc(Graphics g, RectangleF disc, Color top, Color bottom)
        {
            using var light = new LinearGradientBrush(new RectangleF(disc.X, disc.Y - 1, disc.Width, disc.Height + 2), top, bottom, LinearGradientMode.Vertical);
            light.SetBlendTriangularShape(1f, 1f);
            light.Blend = new Blend { Factors = [0f, 0.55f, 1f, 1f], Positions = [0f, 0.45f, 0.85f, 1f] };
            g.FillEllipse(light, disc);
        }

        private GraphicsPath? DigitPath(string text, float emSize)
        {
            if (string.IsNullOrEmpty(text)) return null;
            if (_digit is not null && _digitKey == (text, emSize)) return _digit;
            _digit?.Dispose();
            _digit = new GraphicsPath();
            _digit.AddString(text, WizardFonts.Family(600), (int)FontStyle.Regular, emSize, PointF.Empty, StringFormat.GenericTypographic);
            _digitKey = (text, emSize);
            return _digit;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _digit?.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- progress

    /// <summary>
    /// Live figures for a transfer: a speed chart, then speed, bytes done of total and time
    /// left in columns, with the percentage at the end.
    /// </summary>
    private sealed class TransferStatsView : Control
    {
        private const int HistoryLength = 120;
        private readonly float _chartHeight;
        private readonly List<double> _history = [];
        private (string Caption, string Value)[] _columns = [];
        private string _percent = string.Empty;

        /// <summary>The chart is 72 px over a download and 56 over the write, as in the design.</summary>
        public TransferStatsView(float chartHeight)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            TabStop = false;
            _chartHeight = chartHeight;
            Height = S(chartHeight + 64);
        }

        internal IReadOnlyList<(string Caption, string Value)> Columns => _columns;

        internal int SampleCount => _history.Count;

        internal void SetColumns((string Caption, string Value)[] columns, string percent)
        {
            _columns = columns;
            _percent = percent;
            Invalidate();
        }

        internal void AddSample(double bytesPerSecond)
        {
            _history.Add(Math.Max(0, bytesPerSecond));
            if (_history.Count > HistoryLength) _history.RemoveAt(0);
            Invalidate();
        }

        internal void ResetHistory()
        {
            _history.Clear();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawChart(g, new Rectangle(0, 0, Width, S(_chartHeight)));

            using var captionFont = WizardFonts.Get(12, 600);
            using var valueFont = WizardFonts.Get(20, 600);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            // Under the chart: the caps captions 20 px down, the figures 33 px down, and the
            // percentage on the right, centred on the figures and the captions together.
            var top = S(_chartHeight);
            var percentWidth = S(64);
            var columnWidth = _columns.Length == 0 ? 0 : (Width - percentWidth) / _columns.Length;
            using var divider = new Pen(WizardDesign.FieldBorder);
            var captionHeight = DesignText.MeasureText("A", captionFont, Size.Empty, flags).Height;
            var valueHeight = DesignText.MeasureText("A", valueFont, Size.Empty, flags).Height;
            for (var index = 0; index < _columns.Length; index++)
            {
                var x = index * columnWidth + (index == 0 ? 0 : S(24));
                if (index > 0) g.DrawLine(divider, index * columnWidth, top + S(18), index * columnWidth, top + S(58));
                var caption = _columns[index].Caption.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
                DesignText.Draw(g, caption, captionFont, WizardDesign.TextSecondary, x, top + S(20) + (S(16) - captionHeight) / 2, 0.06f);
                DesignText.DrawText(g, _columns[index].Value, valueFont,
                    new Rectangle(x, top + S(37) + (S(28) - valueHeight) / 2, columnWidth - S(28), valueHeight), WizardDesign.Text,
                    flags | TextFormatFlags.EndEllipsis);
            }

            var percentSize = DesignText.MeasureText(_percent, valueFont, Size.Empty, flags);
            DesignText.DrawText(g, _percent, valueFont, new Point(Width - percentSize.Width, top + S(27) + (S(28) - valueHeight) / 2), WizardDesign.Text, flags);
        }

        private void DrawChart(Graphics g, Rectangle bounds)
        {
            PaintSurface(g, bounds, 10, WizardDesign.Row, WizardDesign.RowBorder);
            using var card = Rounded(new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1), Sf(10));
            var plot = Rectangle.FromLTRB(bounds.Left + S(16), bounds.Top + S(12), bounds.Right - S(16), bounds.Bottom - S(12));
            var state = g.Save();
            g.SetClip(card);
            using (var grid = new Pen(Color.FromArgb(0x2A, 0x28, 0x37)))
            {
                for (var line = 0; line <= 2; line++)
                {
                    var y = plot.Top + plot.Height * line / 2f;
                    g.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
            }

            if (_history.Count >= 2)
            {
                var peak = Math.Max(1, _history.Max()) * 1.15;
                var step = plot.Width / (float)(HistoryLength - 1);
                var start = plot.Right - step * (_history.Count - 1);
                var points = _history
                    .Select((value, index) => new PointF(start + index * step, plot.Bottom - (float)(value / peak) * plot.Height))
                    .ToArray();
                using var area = new GraphicsPath();
                area.AddLine(points[0].X, plot.Bottom, points[0].X, points[0].Y);
                area.AddLines(points);
                area.AddLine(points[^1].X, points[^1].Y, points[^1].X, plot.Bottom);
                area.CloseFigure();
                using (var fill = new SolidBrush(Color.FromArgb(0x2C, 0x29, 0x3A))) g.FillPath(fill, area);
                using var pen = new Pen(Color.FromArgb(0xB4, 0xB4, 0xC6), Sf(1.4f)) { LineJoin = LineJoin.Round };
                g.DrawLines(pen, points);
            }

            g.Restore(state);
        }
    }

    /// <summary>
    /// The design's progress bar: an 8 px track, r4, with a light fill. Busy work with no figure
    /// shows a short segment that sweeps across.
    /// </summary>
    private sealed class WizardProgressBar : Control
    {
        private const double IndeterminatePeriodSeconds = 1.6;
        private int _value;
        private int _maximum = 100;
        private bool _isIndeterminate;
        private double _displayedFraction;
        private readonly System.Windows.Forms.Timer _animationTimer;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan _lastTick;

        public WizardProgressBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = WizardDesign.Window;
            Height = S(8);
            _animationTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _animationTimer.Tick += (_, _) => AdvanceAnimation();
            VisibleChanged += (_, _) => UpdateAnimationState();
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => _value;
            set
            {
                _value = Math.Clamp(value, 0, _maximum);
                UpdateAnimationState();
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Maximum
        {
            get => _maximum;
            set
            {
                _maximum = Math.Max(1, value);
                _value = Math.Min(_value, _maximum);
                UpdateAnimationState();
            }
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            set
            {
                if (_isIndeterminate == value) return;
                _isIndeterminate = value;
                if (!value) _displayedFraction = 0;
                UpdateAnimationState();
            }
        }

        /// <summary>Kept for callers; the design's bar has no shimmer.</summary>
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Shimmer { get; set; }

        private double TargetFraction => Math.Clamp(_value / (double)_maximum, 0d, 1d);

        internal double DisplayedFraction => _displayedFraction;

        private void UpdateAnimationState()
        {
            var needsMotion = _isIndeterminate || Math.Abs(_displayedFraction - TargetFraction) > 0.0005;
            if (needsMotion && Visible && SystemInformation.UIEffectsEnabled && !_animationTimer.Enabled)
            {
                _lastTick = _clock.Elapsed;
                _animationTimer.Start();
            }
            else if ((!needsMotion || !Visible) && _animationTimer.Enabled)
            {
                _animationTimer.Stop();
            }

            if (!Visible || !SystemInformation.UIEffectsEnabled) _displayedFraction = TargetFraction;
            Invalidate();
        }

        private void AdvanceAnimation()
        {
            var now = _clock.Elapsed;
            var delta = Math.Clamp((now - _lastTick).TotalSeconds, 0, 0.1);
            _lastTick = now;
            if (!_isIndeterminate)
            {
                var target = TargetFraction;
                var step = 1 - Math.Exp(-delta * 9);
                _displayedFraction += (target - _displayedFraction) * step;
                if (Math.Abs(target - _displayedFraction) <= 0.0005)
                {
                    _displayedFraction = target;
                    _animationTimer.Stop();
                }
            }

            Invalidate();
        }

        internal static double SweepPosition(double seconds)
        {
            var phase = seconds % IndeterminatePeriodSeconds / IndeterminatePeriodSeconds;
            return phase * phase * (3 - 2 * phase);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _animationTimer.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            if (Width <= 2 || Height <= 2) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var bar = Math.Min(Height, S(8));
            var bounds = new RectangleF(0, (Height - bar) / 2f, Width, bar);
            using (var track = Rounded(bounds, bar / 2f))
            using (var trackBrush = new SolidBrush(Color.FromArgb(0x27, 0x25, 0x34)))
            {
                g.FillPath(trackBrush, track);
            }

            var fraction = IsIndeterminate ? 0.08d : _displayedFraction;
            var fillWidth = (float)(bounds.Width * fraction);
            if (fillWidth < 1) return;
            var left = IsIndeterminate
                ? bounds.Left + (float)((bounds.Width - fillWidth) * SweepPosition(_clock.Elapsed.TotalSeconds))
                : bounds.Left;
            using var fill = Rounded(new RectangleF(left, bounds.Top, Math.Max(bar, fillWidth), bounds.Height), bar / 2f);
            using var brush = new SolidBrush(WizardDesign.Progress);
            g.FillPath(brush, fill);
        }
    }

    /// <summary>The stages of creating a stick, one per line: done, under way, failed, still to come.</summary>
    private sealed class StageChecklist : Control
    {
        private const float LineHeight = 32;
        private string[] _stages = [];
        private int _current = -1;
        private bool _finished;
        private bool _failed;

        public StageChecklist()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            TabStop = false;
        }

        internal IReadOnlyList<string> Stages => _stages;

        internal int Current => _current;

        internal bool Failed => _failed;

        internal void SetStages(string[] stages)
        {
            _stages = stages;
            Height = S(LineHeight) * stages.Length;
            Text = string.Join(Environment.NewLine, stages);
            Invalidate();
        }

        /// <summary>Moves forward only, as the run does.</summary>
        internal void Advance(int index, bool finished = false)
        {
            if (index < _current && !finished) return;
            _current = Math.Clamp(index, 0, Math.Max(0, _stages.Length - 1));
            _finished = finished;
            AccessibleDescription = _current < _stages.Length ? _stages[_current] : null;
            Invalidate();
        }

        /// <summary>Marks the current stage as the one that failed.</summary>
        internal void Fail()
        {
            _failed = true;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var medium = WizardFonts.Get(14, 500);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                | TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
            for (var index = 0; index < _stages.Length; index++)
            {
                var done = index < _current || (_finished && index <= _current);
                var current = index == _current && !_finished;
                var failed = current && _failed;
                var y = index * S(LineHeight);
                var cx = Sf(5);
                var cy = y + S(LineHeight) / 2f;
                if (failed)
                {
                    using var mark = new Pen(WizardDesign.Error, Sf(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.DrawLine(mark, cx - Sf(4), cy - Sf(4), cx + Sf(4), cy + Sf(4));
                    g.DrawLine(mark, cx + Sf(4), cy - Sf(4), cx - Sf(4), cy + Sf(4));
                }
                else if (done)
                {
                    DrawTick(g, WizardDesign.GreenLight, cx, cy, WizardDesign.Scale * 1.05f, 1.6f);
                }
                else if (current)
                {
                    PaintOuterGlow(g, new RectangleF(cx - Sf(4), cy - Sf(4), Sf(8), Sf(8)), 4, WizardDesign.Green, 4, 0.35f, ellipse: true);
                    using var dot = new SolidBrush(WizardDesign.Green);
                    g.FillEllipse(dot, cx - Sf(4), cy - Sf(4), Sf(8), Sf(8));
                }
                else
                {
                    using var ring = new Pen(WizardDesign.FutureStage, Sf(1.2f));
                    g.DrawEllipse(ring, cx - Sf(3.5f), cy - Sf(3.5f), Sf(7), Sf(7));
                }

                // One weight for every line: a bold current line changed width as it moved on.
                // The line under way stands out by its colour and its dot.
                var (font, colour) = failed ? (medium, WizardDesign.Error)
                    : current ? (medium, WizardDesign.Text)
                    : (medium, WizardDesign.TextSecondary);
                DesignText.DrawText(g, _stages[index], font, new Rectangle(S(28), y, Width - S(28), S(LineHeight)), colour, flags);
            }
        }
    }

    /// <summary>
    /// The drive picker of the design: a field with the drive's icon, its name and size, an
    /// optional "external disk" badge, and a chevron; it opens a list of the drives found.
    /// </summary>
    private sealed class UsbDriveField : Control
    {
        private readonly ContextMenuStrip _menu;
        private readonly List<object> _items = [];
        private int _selected = -1;
        private bool _hover;

        public UsbDriveField()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.Transparent;
            Height = S(44);
            Cursor = Cursors.Hand;
            TabStop = true;
            _menu = new ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = WizardDesign.Row,
                ForeColor = WizardDesign.Text,
                Renderer = new ToolStripProfessionalRenderer(new LauncherBranding.MenuColors())
            };
            Click += (_, _) => OpenList();
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal Func<object, (string Title, WizardIcon Icon, string? Badge)>? Describe { get; set; }

        internal event EventHandler? SelectedIndexChanged;

        internal IReadOnlyList<object> Items => _items;

        internal object? SelectedItem => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal int SelectedIndex
        {
            get => _selected;
            set
            {
                var clamped = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
                if (clamped == _selected) return;
                _selected = clamped;
                Text = SelectedItem is { } item ? Describe?.Invoke(item).Title ?? item.ToString() ?? string.Empty : string.Empty;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        internal void SetItems(IEnumerable<object> items, int selected)
        {
            _items.Clear();
            _items.AddRange(items);
            _selected = -2;
            SelectedIndex = _items.Count == 0 ? -1 : selected;
        }

        private void OpenList()
        {
            if (_items.Count < 2) return;
            _menu.Items.Clear();
            using var font = WizardFonts.Get(14, 600);
            for (var index = 0; index < _items.Count; index++)
            {
                var position = index;
                var title = Describe?.Invoke(_items[index]).Title ?? _items[index].ToString() ?? string.Empty;
                var entry = new ToolStripMenuItem(title) { AutoSize = false, Width = Width - 2, Height = S(36), Font = WizardFonts.Get(14, 600), Checked = index == _selected };
                entry.Click += (_, _) => SelectedIndex = position;
                _menu.Items.Add(entry);
            }

            _menu.Show(this, new Point(0, Height + S(4)));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode is Keys.Down && _selected < _items.Count - 1) SelectedIndex = _selected + 1;
            if (e.KeyCode is Keys.Up && _selected > 0) SelectedIndex = _selected - 1;
            if (e.KeyCode is Keys.Space or Keys.Enter or Keys.F4) OpenList();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var g = e.Graphics;
            PaintSurface(g, ClientRectangle, 10, _hover && _items.Count > 1 ? WizardDesign.SecondaryHover : WizardDesign.Row, WizardDesign.RowBorder);
            if (SelectedItem is not { } item) return;
            var (title, icon, badge) = Describe?.Invoke(item) ?? (item.ToString() ?? string.Empty, WizardIcon.Usb, null);
            DrawIcon(g, icon, new RectangleF(Sf(17), 0, Sf(40), Height), WizardDesign.TextSecondary, 16);
            using var font = WizardFonts.Get(14, 600);
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
                | TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis;
            var right = Width - S(48);
            if (!string.IsNullOrEmpty(badge))
            {
                using var badgeFont = WizardFonts.Get(12, 600);
                var caps = badge.ToUpper(System.Globalization.CultureInfo.CurrentCulture);
                var size = DesignText.MeasureText(caps, badgeFont, Size.Empty, TextFormatFlags.NoPadding);
                var width = (int)Math.Ceiling(DesignText.Width(caps, badgeFont, 0.06f, g)) + S(10) * 2;
                var box = new Rectangle(right - width, (Height - S(24)) / 2, width, S(24));
                using (var path = Rounded(new RectangleF(box.X + 0.5f, box.Y + 0.5f, box.Width - 1, box.Height - 1), Sf(6)))
                using (var fill = new SolidBrush(Color.FromArgb(26, WizardDesign.Warning)))
                using (var pen = new Pen(Color.FromArgb(115, WizardDesign.Warning)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }

                DesignText.Draw(g, caps, badgeFont, WizardDesign.WarningText, box.X + S(10), box.Y + (box.Height - size.Height) / 2, 0.06f);
                right = box.X - S(12);
            }

            DesignText.DrawText(g, title, font, new Rectangle(S(72), 0, right - S(72), Height), WizardDesign.Text, flags);
            DrawIcon(g, WizardIcon.ChevronDown, new RectangleF(Width - Sf(26 + 12), 0, Sf(24), Height), WizardDesign.TextSecondary, 10);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _menu.Dispose();
            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------- transitions

    /// <summary>
    /// Covers a region while its contents are rebuilt, then cross-fades to the result.
    /// </summary>
    /// <remarks>
    /// A page is built from many child windows. Built in place, Windows showed each one as
    /// it arrived - white boxes and scroll bars included - for as long as the UI thread was
    /// busy. The overlay keeps the previous picture on screen until the new page has been
    /// laid out, so the operator only ever sees two finished pages and a short blend.
    /// </remarks>
    private sealed class TransitionOverlay : Control
    {
        private readonly Bitmap _from;
        private Bitmap? _to;
        private float _progress;

        public TransitionOverlay(Bitmap from)
        {
            _from = from;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            TabStop = false;
        }

        internal bool HasTarget => _to is not null;

        internal void SetTarget(Bitmap to)
        {
            _to?.Dispose();
            _to = to;
            Invalidate();
        }

        internal void SetProgress(float progress)
        {
            _progress = Math.Clamp(progress, 0f, 1f);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            if (_to is null)
            {
                g.DrawImageUnscaled(_from, 0, 0);
                return;
            }

            g.DrawImageUnscaled(_to, 0, 0);
            var eased = 1f - (1f - _progress) * (1f - _progress);
            var alpha = 1f - eased;
            if (alpha <= 0.01f) return;
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = alpha });
            g.DrawImage(_from, new Rectangle(0, 0, _from.Width, _from.Height),
                0, 0, _from.Width, _from.Height, GraphicsUnit.Pixel, attributes);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _from.Dispose();
                _to?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(240);
    private TransitionOverlay? _transition;
    private Control? _transitionRegion;
    private System.Windows.Forms.Timer? _transitionTimer;
    private readonly System.Diagnostics.Stopwatch _transitionClock = new();

    private bool CanAnimateTransitions =>
        IsHandleCreated && Visible && WindowState != FormWindowState.Minimized
        && SystemInformation.UIEffectsEnabled && !IsDisposed;

    /// <summary>
    /// Freezes what <paramref name="region"/> shows now; the next idle moment blends it
    /// into whatever the region has become.
    /// </summary>
    private void BeginTransition(Control region)
    {
        if (!CanAnimateTransitions || region.Width < 2 || region.Height < 2) return;
        if (_transition is not null)
        {
            if (_transitionRegion == region || _transitionRegion?.Contains(region) == true)
            {
                BeginInvoke(StartCrossfade);
                return;
            }
            FinishTransition();
        }

        var from = Snapshot(region);
        if (from is null) return;
        _transitionRegion = region;
        _transition = new TransitionOverlay(from)
        {
            Name = "WizardTransitionOverlay",
            Bounds = RectangleToClient(region.RectangleToScreen(region.ClientRectangle))
        };
        Controls.Add(_transition);
        _transition.BringToFront();
        _transition.Update();
        BeginInvoke(StartCrossfade);
    }

    private void StartCrossfade()
    {
        if (_transition is null || _transitionRegion is null || IsDisposed) return;
        _transitionRegion.PerformLayout();
        var to = Snapshot(_transitionRegion);
        if (to is null)
        {
            FinishTransition();
            return;
        }

        _transition.SetTarget(to);
        _transitionTimer ??= CreateTransitionTimer();
        _transitionClock.Restart();
        _transitionTimer.Start();
    }

    private System.Windows.Forms.Timer CreateTransitionTimer()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        timer.Tick += (_, _) =>
        {
            if (_transition is null)
            {
                timer.Stop();
                return;
            }

            var progress = (float)(_transitionClock.Elapsed / TransitionDuration);
            if (progress >= 1f)
            {
                FinishTransition();
                return;
            }
            _transition.SetProgress(progress);
        };
        return timer;
    }

    private void FinishTransition()
    {
        _transitionTimer?.Stop();
        var overlay = _transition;
        _transition = null;
        _transitionRegion = null;
        if (overlay is null) return;
        Controls.Remove(overlay);
        overlay.Dispose();
    }

    private static Bitmap? Snapshot(Control region)
    {
        try
        {
            var bitmap = new Bitmap(region.Width, region.Height);
            region.DrawToBitmap(bitmap, new Rectangle(0, 0, region.Width, region.Height));
            return bitmap;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

internal sealed partial class LauncherWizardWindow
{
    /// <summary>A status medallion on its own: green, orange or red.</summary>
    private sealed class RowStatusMark : Control
    {
        private RowStatus.Kind _kind = RowStatus.Kind.Ok;

        public RowStatusMark()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Size = S(32, 32);
            TabStop = false;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        internal RowStatus.Kind Kind
        {
            get => _kind;
            set { _kind = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            var disc = new RectangleF((Width - Sf(24)) / 2f, (Height - Sf(24)) / 2f, Sf(24), Sf(24));
            PaintMedallion(e.Graphics, disc, _kind switch
            {
                RowStatus.Kind.Warning => Tone.Warning,
                RowStatus.Kind.Error => Tone.Error,
                _ => Tone.Success
            }, 6);
        }
    }

    /// <summary>
    /// The drive check of step 4: a medallion and the verdict, a hairline, then the drive's
    /// facts in four columns - and, for a slow stick, a line about its speed.
    /// </summary>
    private sealed class UsbCheckRow : Panel
    {
        public UsbCheckRow(int width)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Width = width;
            Margin = S(0, 0, 0, 8);
            Mark = new RowStatusMark { Name = "WizardUsbStatusMark", Location = new Point(S(20), S(8)) };
            Title = new WizardText(16, 600, 20, WizardDesign.Text) { Name = "WizardUsbStatus", Location = new Point(S(72), S(14)) };
            Line = new Divider { Location = new Point(S(72), S(46)), Width = width - S(72 + 16) };
            Facts = new FactGrid { Name = "WizardUsbFacts", Location = new Point(S(72), S(58)), Width = width - S(72) };
            Details = new WizardText(14, 400, 20, WizardDesign.TextSecondary) { Name = "WizardUsbDetails", WrapWidth = width - S(72 + 16) };
            Controls.AddRange([Mark, Title, Line, Facts, Details]);
            Title.TextChanged += (_, _) => Relayout();
            Details.TextChanged += (_, _) => Relayout();
            Facts.VisibleChanged += (_, _) => Relayout();
            Relayout();
        }

        internal RowStatusMark Mark { get; }

        internal WizardText Title { get; }

        internal Divider Line { get; }

        internal FactGrid Facts { get; }

        internal WizardText Details { get; }

        private void Relayout()
        {
            var y = S(46);
            Line.Visible = Facts.Visible || Details.Text.Length > 0;
            y += S(12);
            if (Facts.Visible)
            {
                Facts.Top = y;
                y += Facts.Height + S(8);
            }

            Details.Visible = Details.Text.Length > 0;
            if (Details.Visible)
            {
                Details.Location = new Point(S(72), y);
                y += Details.Height + S(10);
            }

            Height = Line.Visible ? Math.Max(y + S(1), S(60)) : S(60);
            Text = Title.Text;
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintGlassParent(this, e, InvokePaintBackground);
            PaintSurface(e.Graphics, ClientRectangle, 10, WizardDesign.Row, WizardDesign.RowBorder);
        }
    }

    /// <summary>
    /// The empty USB step: the stick's tile in the middle, what to do in a line, why under
    /// it, and - once the search has answered - a refresh button.
    /// </summary>
    private sealed class EmptyState : Panel
    {
        private readonly IconTile _tile = new(WizardIcon.Usb);
        private readonly WizardText _title = new(16, 600, 20, WizardDesign.Text) { Name = "WizardEmptyTitle" };
        private readonly WizardText _detail = new(14, 400, 20, WizardDesign.TextSecondary) { Name = "WizardEmptyDetail" };
        private readonly GlassButton _button = new() { Name = "WizardEmptyRefresh", Kind = ButtonKind.Outline, LeadingIcon = WizardIcon.Refresh, Visible = false };
        private Func<Task>? _action;

        public EmptyState(int width, string title, string detail, string? action)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
            BackColor = Color.Transparent;
            Width = width;
            Margin = Padding.Empty;
            _button.Height = S(32);
            _button.MinimumWidth = 1;
            _button.Click += async (_, _) => { if (_action is not null) await _action(); };
            Controls.AddRange([_tile, _title, _detail, _button]);
            Set(title, detail, action, null);
        }

        internal void Set(string title, string detail, string? action, Func<Task>? onClick)
        {
            _title.Text = title;
            _detail.Text = detail;
            _detail.WrapWidth = 0;
            _action = onClick;
            _button.Visible = action is not null;
            if (action is not null)
            {
                _button.Text = action;
                _button.FitWidth();
            }

            Text = title + Environment.NewLine + detail;
            var y = S(18);
            _tile.Location = new Point((Width - _tile.Width) / 2, y);
            y += _tile.Height + S(16);
            _title.Location = new Point((Width - _title.Width) / 2, y);
            y += _title.Height + S(4);
            _detail.Location = new Point((Width - _detail.Width) / 2, y);
            y += _detail.Height;
            if (_button.Visible)
            {
                y += S(20);
                _button.Location = new Point((Width - _button.Width) / 2, y);
                y += _button.Height;
            }

            Height = y + S(16);
        }
    }
}
