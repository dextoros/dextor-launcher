using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DexTor.Launcher;

/// <summary>
/// The wizard's design tokens and drawing primitives, from the designer's setup wizard UI kit
/// (2026-10-06) and the screen mock-ups it was made with.
/// </summary>
/// <remarks>
/// Sizes are design pixels of the 960 x 680 window; <see cref="S(float)"/> turns them into
/// device pixels for the display the launcher runs on. Where the kit's prose and the screens
/// disagree, the screens win: the kit says so itself (its values "match the screen
/// mock-ups"), and the screens were measured.
/// </remarks>
internal static partial class WizardDesign
{
    // Surfaces.
    /// <summary>The design's #0E0F13, tinted a little by the wallpaper - see <see cref="MicaTint"/>.</summary>
    public static readonly Color Window = MicaTint(Color.FromArgb(0x0E, 0x0F, 0x13));
    public static readonly Color Card = Color.FromArgb(0x16, 0x15, 0x1E);
    public static readonly Color Row = Color.FromArgb(0x1D, 0x1B, 0x28);
    public static readonly Color Tile = Color.FromArgb(0x27, 0x24, 0x36);
    public static readonly Color GoFace = Color.FromArgb(0x12, 0x14, 0x1A);
    public static readonly Color Secondary = Color.FromArgb(0x1C, 0x1A, 0x23);
    public static readonly Color SecondaryHover = Color.FromArgb(0x23, 0x20, 0x2B);
    public static readonly Color SecondaryPressed = Color.FromArgb(0x18, 0x16, 0x1E);

    // Borders and text.
    public static readonly Color CardBorder = Color.FromArgb(0x2C, 0x2A, 0x3A);
    public static readonly Color RowBorder = Color.FromArgb(0x32, 0x2F, 0x42);
    public static readonly Color FieldBorder = Color.FromArgb(0x3B, 0x3B, 0x4B);
    public static readonly Color Divider = FieldBorder;
    public static readonly Color Text = Color.FromArgb(0xF2, 0xF2, 0xF5);
    public static readonly Color TextSecondary = Color.FromArgb(0xA6, 0xA6, 0xBC);
    public static readonly Color TextDisabled = Color.FromArgb(0x6A, 0x6A, 0x7A);
    public static readonly Color Link = Color.FromArgb(0xC4, 0xC4, 0xD4);

    // Brand and service colours.
    public static readonly Color Brand = Color.FromArgb(0x94, 0x55, 0xF2);
    public static readonly Color Progress = Color.FromArgb(0xE7, 0xE7, 0xEC);
    public static readonly Color FutureStage = Color.FromArgb(0x5A, 0x5A, 0x6E);
    public static readonly Color Overlay = Color.FromArgb(0x06, 0x07, 0x0A);
    public const double OverlayOpacity = 0.72;
    public static readonly Color Danger = Color.FromArgb(0xD1, 0x43, 0x43);
    public static readonly Color DangerHover = Color.FromArgb(0xDB, 0x50, 0x50);
    public static readonly Color DangerPressed = Color.FromArgb(0xBC, 0x3B, 0x3B);

    // Statuses.
    public static readonly Color Green = Color.FromArgb(0x3D, 0xDC, 0x97);
    public static readonly Color GreenLight = Color.FromArgb(0x7A, 0xF0, 0xBE);
    public static readonly Color GreenTop = Color.FromArgb(0x5B, 0xE6, 0xA8);
    public static readonly Color GreenBottom = Color.FromArgb(0x2F, 0xBF, 0x80);
    public static readonly Color GreenDeep = Color.FromArgb(0x1E, 0x9A, 0x62);
    public static readonly Color Warning = Color.FromArgb(0xF2, 0x8C, 0x28);
    public static readonly Color WarningText = Color.FromArgb(0xF5, 0xA0, 0x4A);
    public static readonly Color Error = Color.FromArgb(0xE0, 0x64, 0x64);
    public static readonly Color ErrorTop = Color.FromArgb(0xF0, 0x8A, 0x8A);
    public static readonly Color ErrorBottom = Color.FromArgb(0xD4, 0x4C, 0x4C);

    /// <summary>Kept for the parts of the launcher outside the wizard that still use them.</summary>
    public static readonly Color GreenText = GreenLight;
    public static readonly Color ErrorBorder = Color.FromArgb(128, 0xE0, 0x64, 0x64);
    public static readonly Color WarningBorder = Color.FromArgb(115, 0xF2, 0x8C, 0x28);

    /// <summary>The design's inner top highlight: inset 0 1px 0 rgba(214,196,255,0.07).</summary>
    private static readonly Color Highlight = Color.FromArgb(18, 0xD6, 0xC4, 0xFF);

    /// <summary>Device pixels per design pixel: 1.25 on a 125 % display.</summary>
    public static float Scale { get; } = ReadScale();

    public static int S(float designPixels) => (int)Math.Round(designPixels * Scale);

    public static float Sf(float designPixels) => designPixels * Scale;

    public static Size S(float width, float height) => new(S(width), S(height));

    public static Padding S(float left, float top, float right, float bottom) => new(S(left), S(top), S(right), S(bottom));

    /// <summary>
    /// The display scale, but never more than lets the 960 x 648 window - title bar and a
    /// margin included - fit the screen's work area: at 150% on a 1366 x 768 laptop it would
    /// have been 1440 x 1017 on a 728-pixel-high desktop.
    /// </summary>
    internal static float FitScale(float dpiScale, int workWidth, int workHeight) =>
        workWidth <= 0 || workHeight <= 0
            ? dpiScale
            : Math.Max(0.75f, Math.Min(dpiScale, Math.Min(workWidth / 980f, workHeight / 700f)));

    private static float ReadScale()
    {
        try
        {
            using var screen = Graphics.FromHwnd(IntPtr.Zero);
            var dpi = Math.Max(1f, screen.DpiX / 96f);
            var work = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
            return FitScale(dpi, work.Width, work.Height);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or OutOfMemoryException or ArgumentException)
        {
            return 1f;
        }
    }

    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        if (diameter <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// A card, a row, a field: a fill, a hairline border and - <paramref name="highlight"/> - the
    /// design's faint violet line along the inside of the top edge. Radius in design pixels.
    /// </summary>
    public static void PaintSurface(Graphics g, Rectangle bounds, float radius, Color fill, Color border, bool highlight = true)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = Sf(radius);
        var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1);
        using var path = Rounded(rect, r);
        if (fill.A > 0)
        {
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        if (highlight)
        {
            var state = g.Save();
            g.SetClip(path);
            using var shine = new Pen(Highlight, Math.Max(1f, Scale));
            g.DrawLine(shine, rect.Left + r * 0.6f, rect.Top + Scale, rect.Right - r * 0.6f, rect.Top + Scale);
            g.Restore(state);
        }

        if (border.A > 0)
        {
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }
    }

    /// <summary>
    /// A soft glow around <paramref name="bounds"/>, the way a CSS box-shadow with blur spreads:
    /// <paramref name="blur"/> design pixels wide, starting at <paramref name="alpha"/>.
    /// </summary>
    public static void PaintOuterGlow(Graphics g, RectangleF bounds, float radius, Color color, float blur, float alpha, bool ellipse = false)
    {
        if (blur <= 0 || alpha <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (ellipse)
        {
            PaintRadialGlow(g, bounds, color, blur, alpha);
            return;
        }

        var steps = Math.Max(4, (int)Math.Ceiling(Sf(blur)));
        for (var step = steps; step >= 1; step--)
        {
            var spread = step;
            var t = 1f - (step - 0.5f) / steps;
            // Gaussian-like fall-off: dense near the edge, thin at the far end.
            var a = alpha * t * t / steps * 3.2f;
            var ring = RectangleF.Inflate(bounds, spread, spread);
            using var brush = new SolidBrush(Color.FromArgb(Math.Clamp((int)(a * 255), 0, 255), color));
            if (ellipse)
            {
                g.FillEllipse(brush, ring);
            }
            else
            {
                using var path = Rounded(ring, Sf(radius) + spread);
                g.FillPath(brush, path);
            }
        }
    }

    /// <summary>
    /// The glow of a disc as one radial gradient. Stacked rings - each a whole device pixel
    /// wider and a few alpha steps fainter - showed as bands around the small medallions;
    /// the gradient falls off the way the rings added up (cubic) but without the steps.
    /// </summary>
    private static void PaintRadialGlow(Graphics g, RectangleF disc, Color color, float blur, float alpha)
    {
        var spread = Sf(blur);
        var halo = RectangleF.Inflate(disc, spread, spread);
        var radius = Math.Min(halo.Width, halo.Height) / 2;
        if (radius <= 0) return;
        var peak = Math.Clamp(alpha * 3.2f / 3f, 0f, 1f);
        // Blend positions run from the halo's rim (0) to its centre (1); the disc hides the rest.
        var rim = Math.Clamp(spread / radius, 0.01f, 0.99f);
        const int Points = 16;
        var positions = new float[Points + 1];
        var factors = new float[Points + 1];
        for (var i = 0; i < Points; i++)
        {
            var t = i / (float)(Points - 1);
            positions[i] = rim * t;
            factors[i] = t * t * t;
        }

        positions[Points] = 1f;
        factors[Points] = 1f;
        using var path = new GraphicsPath();
        path.AddEllipse(halo);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb((int)(peak * 255), color),
            SurroundColors = [Color.FromArgb(0, color)],
            Blend = new Blend(Points + 1) { Positions = positions, Factors = factors }
        };
        g.FillPath(brush, path);
    }

    /// <summary>
    /// Fills <paramref name="path"/> lit from <paramref name="light"/>: <paramref name="top"/>
    /// there, <paramref name="bottom"/> at the edge. A path gradient drops the anti-aliased
    /// fringe of its own path, which left small discs with stair-stepped edges; so the edge
    /// colour goes down first as a plain anti-aliased fill and the gradient lands inside it.
    /// </summary>
    public static void FillLit(Graphics g, GraphicsPath path, PointF light, Color top, Color bottom, float focus)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var edge = new SolidBrush(bottom)) g.FillPath(edge, path);
        using var gradient = new PathGradientBrush(path)
        {
            CenterColor = top,
            CenterPoint = light,
            SurroundColors = [bottom],
            FocusScales = new PointF(focus, focus)
        };
        g.FillPath(gradient, path);
    }

    /// <summary>A glow along the inside of the edge: inset 0 0 <paramref name="blur"/> colour.</summary>
    public static void PaintInnerGlow(Graphics g, RectangleF bounds, float radius, Color color, float blur, float alpha)
    {
        if (blur <= 0 || alpha <= 0) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var clip = Rounded(bounds, Sf(radius));
        var state = g.Save();
        g.SetClip(clip, CombineMode.Intersect);
        var steps = Math.Max(3, (int)Math.Ceiling(Sf(blur)));
        for (var step = 0; step < steps; step++)
        {
            var t = 1f - step / (float)steps;
            using var pen = new Pen(Color.FromArgb(Math.Clamp((int)(alpha * t * t * 255), 0, 255), color), 1.2f);
            using var ring = Rounded(RectangleF.Inflate(bounds, -step - 0.5f, -step - 0.5f), Math.Max(1, Sf(radius) - step));
            g.DrawPath(pen, ring);
        }

        g.Restore(state);
    }

    internal enum ButtonLook { Normal, Hover, Pressed }

    /// <summary>
    /// The primary ("go") button: a dark face, a 2 px border running from deep to light green,
    /// an outer and an inner green glow that grow on hover and shrink when pressed. On hover
    /// the face also takes a green tint and the whole border lights up, so the button
    /// plainly answers the pointer.
    /// </summary>
    /// <param name="outerGlow">False when the surface under the button paints the halo.</param>
    public static void PaintGoButton(Graphics g, RectangleF bounds, ButtonLook state, bool outerGlow = true)
    {
        if (outerGlow) PaintGoGlow(g, bounds, state);
        using (var path = Rounded(bounds, Sf(8)))
        using (var face = new SolidBrush(state == ButtonLook.Hover ? Blend(GoFace, Green, 0.08f) : GoFace))
        {
            g.FillPath(face, path);
        }

        var (_, _, inner, innerAlpha) = GoGlow(state);
        PaintInnerGlow(g, bounds, 8, Green, inner, innerAlpha);
        var width = Math.Max(1.5f, Sf(1.6f));
        var edge = RectangleF.Inflate(bounds, -width / 2, -width / 2);
        var (from, to) = state == ButtonLook.Hover ? (Green, Color.FromArgb(0xA8, 0xF7, 0xD4)) : (GreenDeep, GreenLight);
        using var border = new LinearGradientBrush(bounds, from, to, LinearGradientMode.Horizontal);
        using var pen = new Pen(border, width);
        using var outline = Rounded(edge, Sf(8) - width / 2);
        g.DrawPath(pen, outline);
    }

    /// <summary>How far, in design pixels, the go button's glow reaches at its widest (hover).</summary>
    public const float GoGlowReach = 22f;

    /// <summary>The go button's outer and inner glow: blur in design pixels and strength.</summary>
    private static (float Outer, float OuterAlpha, float Inner, float InnerAlpha) GoGlow(ButtonLook state) => state switch
    {
        ButtonLook.Hover => (GoGlowReach, 0.95f, 16f, 0.34f),
        ButtonLook.Pressed => (8f, 0.25f, 8f, 0.12f),
        _ => (14f, 0.35f, 10f, 0.15f)
    };

    /// <summary>
    /// The go button's outer glow on its own. The button paints it inside its bounds and the
    /// parent around them, so the halo is continuous instead of cut off square at the edges
    /// of the control - which showed as grey corners beside the button.
    /// </summary>
    public static void PaintGoGlow(Graphics g, RectangleF bounds, ButtonLook state)
    {
        var (outer, outerAlpha, _, _) = GoGlow(state);
        PaintOuterGlow(g, bounds, 8, Green, outer, outerAlpha * 0.55f);
    }

    /// <summary>The secondary button and the language list: dark, with a faint violet inner light.</summary>
    public static void PaintSecondaryButton(Graphics g, RectangleF bounds, ButtonLook state)
    {
        var fill = state switch { ButtonLook.Hover => SecondaryHover, ButtonLook.Pressed => SecondaryPressed, _ => Secondary };
        using (var path = Rounded(bounds, Sf(8)))
        using (var face = new SolidBrush(fill))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(face, path);
        }

        if (state == ButtonLook.Normal) PaintInnerGlow(g, RectangleF.Inflate(bounds, -1, -1), 8, Color.FromArgb(0xD6, 0xC4, 0xFF), 12, 0.06f);
        using (var inset = new Pen(Color.FromArgb(26, 0xD6, 0xC4, 0xFF)))
        using (var ring = Rounded(RectangleF.Inflate(bounds, -1.5f, -1.5f), Sf(8) - 1))
        {
            g.DrawPath(inset, ring);
        }

        using var edge = new Pen(Color.FromArgb(89, 0, 0, 0));
        using var outline = Rounded(RectangleF.Inflate(bounds, -0.5f, -0.5f), Sf(8));
        g.DrawPath(edge, outline);
    }

    /// <summary>Every button that cannot be pressed: the same shape, the plain row colours.</summary>
    public static void PaintDisabledButton(Graphics g, RectangleF bounds)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(RectangleF.Inflate(bounds, -0.5f, -0.5f), Sf(8));
        using var face = new SolidBrush(Row);
        g.FillPath(face, path);
        using var pen = new Pen(CardBorder);
        g.DrawPath(pen, path);
    }

    /// <summary>The red button of the erase dialog: a flat fill, no border.</summary>
    public static void PaintDangerButton(Graphics g, RectangleF bounds, ButtonLook state)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(bounds, Sf(8));
        using var face = new SolidBrush(state switch { ButtonLook.Hover => DangerHover, ButtonLook.Pressed => DangerPressed, _ => Danger });
        g.FillPath(face, path);
    }

    /// <summary>"Abort": a transparent button with a red outline and red text.</summary>
    public static void PaintDangerOutlineButton(Graphics g, RectangleF bounds, ButtonLook state)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(RectangleF.Inflate(bounds, -0.5f, -0.5f), Sf(8));
        if (state != ButtonLook.Normal)
        {
            using var face = new SolidBrush(Color.FromArgb(state == ButtonLook.Pressed ? 31 : 20, Danger));
            g.FillPath(face, path);
        }

        using var pen = new Pen(Color.FromArgb(115, Danger));
        g.DrawPath(pen, path);
    }

    internal enum Tone { Success, Warning, Error, Neutral }

    /// <summary>
    /// A status medallion: a small lit disc with a tick, an exclamation mark or a cross, in
    /// the colour of the status, with a glow of the same colour.
    /// </summary>
    public static void PaintMedallion(Graphics g, RectangleF disc, Tone tone, float glow = 6)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (top, bottom) = tone switch
        {
            Tone.Warning => (Color.FromArgb(0xFF, 0xB4, 0x5C), Color.FromArgb(0xE8, 0x82, 0x1E)),
            Tone.Error => (ErrorTop, ErrorBottom),
            _ => (GreenTop, GreenBottom)
        };
        var glowColour = tone switch { Tone.Warning => Warning, Tone.Error => Error, _ => Green };
        PaintOuterGlow(g, disc, disc.Width / 2, glowColour, glow, 0.30f, ellipse: true);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(disc);
            FillLit(g, path, new PointF(disc.Left + disc.Width * 0.35f, disc.Top + disc.Height * 0.25f), top, bottom, 0.1f);
        }

        var unit = disc.Width / 20f;
        var cx = disc.Left + disc.Width / 2;
        var cy = disc.Top + disc.Height / 2;
        switch (tone)
        {
            case Tone.Warning:
            {
                using var ink = new SolidBrush(Color.FromArgb(0x2A, 0x16, 0x05));
                g.FillRectangle(ink, cx - 0.95f * unit, cy - 5.2f * unit, 1.9f * unit, 6.6f * unit);
                g.FillEllipse(ink, cx - 1.1f * unit, cy + 2.6f * unit, 2.2f * unit, 2.2f * unit);
                break;
            }
            case Tone.Error:
            {
                using var pen = new Pen(Color.White, 2f * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawLine(pen, cx - 3.6f * unit, cy - 3.6f * unit, cx + 3.6f * unit, cy + 3.6f * unit);
                g.DrawLine(pen, cx + 3.6f * unit, cy - 3.6f * unit, cx - 3.6f * unit, cy + 3.6f * unit);
                break;
            }
            default:
                DrawTick(g, Color.White, cx, cy, unit, 2.1f);
                break;
        }
    }

    /// <summary>A tick centred on (cx, cy), in a 20-unit box.</summary>
    public static void DrawTick(Graphics g, Color colour, float cx, float cy, float unit, float weight, float fraction = 1f)
    {
        using var pen = new Pen(colour, weight * unit) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        PointF[] tick = [new(cx - 4.4f * unit, cy + 0.2f * unit), new(cx - 1.3f * unit, cy + 3.2f * unit), new(cx + 4.6f * unit, cy - 3.2f * unit)];
        DrawPartial(g, pen, tick, fraction);
    }

    /// <summary>Draws a polyline only as far as <paramref name="fraction"/> of its length.</summary>
    public static void DrawPartial(Graphics g, Pen pen, PointF[] points, float fraction)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (fraction >= 1f)
        {
            g.DrawLines(pen, points);
            return;
        }

        var lengths = new float[points.Length - 1];
        var total = 0f;
        for (var i = 0; i < lengths.Length; i++)
        {
            lengths[i] = MathF.Sqrt(MathF.Pow(points[i + 1].X - points[i].X, 2) + MathF.Pow(points[i + 1].Y - points[i].Y, 2));
            total += lengths[i];
        }

        var remaining = total * fraction;
        var drawn = new List<PointF> { points[0] };
        for (var i = 0; i < lengths.Length && remaining > 0; i++)
        {
            var part = Math.Min(1f, remaining / lengths[i]);
            drawn.Add(new PointF(points[i].X + (points[i + 1].X - points[i].X) * part, points[i].Y + (points[i + 1].Y - points[i].Y) * part));
            remaining -= lengths[i];
        }

        if (drawn.Count > 1) g.DrawLines(pen, drawn.ToArray());
    }

    /// <summary>
    /// The check that is still running: a quiet ring with a light arc on it. It does not spin -
    /// the design allows no waiting animation.
    /// </summary>
    public static void PaintChecking(Graphics g, RectangleF disc)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var inset = RectangleF.Inflate(disc, -Sf(1.5f), -Sf(1.5f));
        using (var ring = new Pen(FutureStage, Sf(1.5f))) g.DrawEllipse(ring, inset);
        using var arc = new Pen(Text, Sf(2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, inset, 120, 110);
    }

    /// <summary>The check that has not started: an empty ring.</summary>
    public static void PaintPending(Graphics g, RectangleF disc)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var ring = new Pen(CardBorder, Sf(1.5f));
        g.DrawEllipse(ring, RectangleF.Inflate(disc, -Sf(1f), -Sf(1f)));
    }

    /// <summary>A rounded square lit from the top-left - the icon tile of error blocks and dialogs.</summary>
    public static void PaintLitTile(Graphics g, RectangleF bounds, Color top, Color bottom, Color glow)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        PaintOuterGlow(g, bounds, 8, glow, 10, 0.35f);
        using var path = Rounded(bounds, Sf(8));
        FillLit(g, path, new PointF(bounds.Left + bounds.Width * 0.3f, bounds.Top + bounds.Height * 0.2f), top, bottom, 0.15f);
    }

    /// <summary>
    /// The orange and red blocks: a horizontal fade of the tone over the row colour, a tinted
    /// border and a soft glow of the same tone.
    /// </summary>
    public static void PaintToneBlock(Graphics g, Rectangle bounds, Tone tone)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var (colour, from, to, border) = tone switch
        {
            Tone.Error => (Error, 0.16f, 0.04f, 0.50f),
            Tone.Success => (Green, 0.14f, 0.05f, 0.45f),
            _ => (Warning, 0.16f, 0.05f, 0.45f)
        };
        var rect = new RectangleF(bounds.X + 0.5f, bounds.Y + 0.5f, bounds.Width - 1, bounds.Height - 1);
        PaintOuterGlow(g, rect, 10, colour, 16, 0.14f * 0.5f);
        using var path = Rounded(rect, Sf(10));
        using (var under = new SolidBrush(Card)) g.FillPath(under, path);
        using (var fade = new LinearGradientBrush(rect, Color.FromArgb((int)(from * 255), colour), Color.FromArgb((int)(to * 255), colour), LinearGradientMode.Horizontal))
        {
            g.FillPath(fade, path);
        }

        using var pen = new Pen(Color.FromArgb((int)(border * 255), colour));
        g.DrawPath(pen, path);
    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        Math.Clamp((int)Math.Round(from.R + (to.R - from.R) * amount), 0, 255),
        Math.Clamp((int)Math.Round(from.G + (to.G - from.G) * amount), 0, 255),
        Math.Clamp((int)Math.Round(from.B + (to.B - from.B) * amount), 0, 255));

    /// <summary>
    /// Text with extra space between letters - the design's caps captions and badges
    /// (letter-spacing 0.06em), which GDI cannot do on its own.
    /// </summary>
    public static void DrawSpacedText(Graphics g, string text, Font font, Color colour, Point origin, float spacingEm)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left | TextFormatFlags.Top;
        var extra = font.SizeInPoints / 72f * g.DpiY * spacingEm;
        float x = origin.X;
        foreach (var ch in text)
        {
            var glyph = ch.ToString();
            TextRenderer.DrawText(g, glyph, font, new Point((int)Math.Round(x), origin.Y), colour, flags);
            x += TextRenderer.MeasureText(g, glyph, font, Size.Empty, flags).Width + extra;
        }
    }

    public static Size MeasureSpacedText(Graphics? g, string text, Font font, float spacingEm)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        var pixelsPerPoint = (g?.DpiY ?? 96f * Scale) / 72f;
        var extra = font.SizeInPoints * pixelsPerPoint * spacingEm;
        float width = 0;
        var height = 0;
        foreach (var ch in text)
        {
            var size = g is null
                ? TextRenderer.MeasureText(ch.ToString(), font, Size.Empty, flags)
                : TextRenderer.MeasureText(g, ch.ToString(), font, Size.Empty, flags);
            width += size.Width + extra;
            height = Math.Max(height, size.Height);
        }

        return new Size((int)Math.Ceiling(Math.Max(0, width - extra)), height);
    }
}

internal static partial class WizardDesign
{
    private const float MicaStrength = 0.08f;

    /// <summary>
    /// The window colour with a little of the wallpaper's average colour in it, the way
    /// Windows 11's Mica material tints a window: opaque, nothing behind it shows through,
    /// but it sits in the desktop's colours. The design colour itself when the wallpaper
    /// cannot be read (a solid colour, a slideshow, a locked-down machine).
    /// </summary>
    /// <remarks>
    /// The window used to be 96 % opaque and showed the desktop sharply through every card;
    /// asked for "transparency as in Windows", Mica was chosen over a see-through blur.
    /// </remarks>
    internal static Color MicaTint(Color baseColor)
    {
        try
        {
            var buffer = new char[520];
            if (!SystemParametersInfo(SpiGetDeskWallpaper, (uint)buffer.Length, ref buffer[0], 0)) return baseColor;
            var path = new string(buffer).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return baseColor;
            using var wallpaper = Image.FromFile(path);
            using var sample = new Bitmap(16, 16);
            using (var graphics = Graphics.FromImage(sample))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(wallpaper, 0, 0, 16, 16);
            }

            long r = 0, gr = 0, b = 0;
            for (var y = 0; y < 16; y++)
            {
                for (var x = 0; x < 16; x++)
                {
                    var pixel = sample.GetPixel(x, y);
                    r += pixel.R;
                    gr += pixel.G;
                    b += pixel.B;
                }
            }

            return Blend(baseColor, Color.FromArgb((int)(r / 256), (int)(gr / 256), (int)(b / 256)), MicaStrength);
        }
        catch (Exception exception) when (exception is IOException or OutOfMemoryException or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.ExternalException)
        {
            return baseColor;
        }
    }

    /// <summary>
    /// Makes windows created on this thread system-DPI aware, as the launcher's process is
    /// (Program.Main). For a host that is not - the screenshot tests.
    /// </summary>
    internal static void UseSystemDpiOnThisThread() => _ = SetThreadDpiAwarenessContext(new IntPtr(-2));

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    private const uint SpiGetDeskWallpaper = 0x0073;

    [System.Runtime.InteropServices.LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true, StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SystemParametersInfo(uint action, uint parameter, ref char value, uint winIni);
}
