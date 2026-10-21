using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DexTor.Launcher;

/// <summary>
/// Text set the way the design sets it: a CSS-like line box (<see cref="LineHeight"/>), word
/// wrapping at a given width, letter spacing, and the product name in the brand violet.
/// </summary>
/// <remarks>
/// A plain label places text by the font's own box and wraps with GDI's line spacing, so
/// every block came out a few pixels off the mock-ups and two-line texts were too tight.
/// Here each line is centred in its line box exactly as a browser does: the baseline sits at
/// half the leading plus the ascent.
/// </remarks>
internal sealed partial class WizardText : Label
{
    private const string BrandName = "DexTor OS";
    private float _lineHeight;
    private int _wrapWidth;
    private float _spacing;
    private bool _caps;
    private string? _brand;
    private List<string>? _lines;

    public WizardText(float pixels, int weight, float lineHeight, Color colour)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Font = WizardFonts.Get(pixels, weight);
        ForeColor = colour;
        _lineHeight = lineHeight;
        UseMnemonic = false;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        AutoSize = true;
    }

    /// <summary>The line box, in design pixels.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal float LineHeight
    {
        get => _lineHeight;
        set { _lineHeight = value; Reflow(); }
    }

    /// <summary>Wraps at this many device pixels; 0 keeps the text on one line (with an ellipsis if it must).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int WrapWidth
    {
        get => _wrapWidth;
        set { _wrapWidth = value; Reflow(); }
    }

    /// <summary>Extra space between letters, in em: 0.06 for the caps captions, -0.01 for titles.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal float LetterSpacing
    {
        get => _spacing;
        set { _spacing = value; Reflow(); }
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Caps
    {
        get => _caps;
        set { _caps = value; Reflow(); }
    }

    /// <summary>When set, this part of the text is drawn in the brand violet ("DexTor OS" in titles).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool HighlightBrand
    {
        get => _brand is not null;
        set { _brand = value ? BrandName : null; Invalidate(); }
    }

    private string Shown => _caps ? Text.ToUpper(System.Globalization.CultureInfo.CurrentCulture) : Text;

    private int LinePixels => Math.Max(1, WizardDesign.S(_lineHeight));

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Reflow();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Reflow();
    }

    private void Reflow()
    {
        _lines = null;
        if (AutoSize) Size = GetPreferredSize(Size.Empty);
        Invalidate();
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var lines = Lines();
        var width = _wrapWidth > 0 ? _wrapWidth : lines.Select(Measure).DefaultIfEmpty(0).Max() + 1;
        return new Size(width, Math.Max(1, lines.Count) * LinePixels);
    }

    internal IReadOnlyList<string> Lines()
    {
        if (_lines is not null) return _lines;
        var lines = new List<string>();
        foreach (var paragraph in Shown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (_wrapWidth <= 0)
            {
                lines.Add(paragraph);
                continue;
            }

            var line = string.Empty;
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Measure(candidate) > _wrapWidth)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }

            lines.Add(line);
        }

        return _lines = lines;
    }

    private int Measure(string text)
    {
        if (text.Length == 0) return 0;
        return (int)Math.Ceiling(DesignText.Width(text, Font, SpacingEm));
    }

    private float SpacingEm => _spacing;

    protected override void OnPaint(PaintEventArgs e)
    {
        var lines = Lines();
        var fontHeight = DesignText.FontHeight(Font);
        var line = LinePixels;
        var offset = (line - fontHeight) / 2f;
        for (var index = 0; index < lines.Count; index++)
        {
            var y = (int)Math.Round(index * line + offset);
            DrawLine(e.Graphics, lines[index], y);
        }
    }

    private void DrawLine(Graphics g, string text, int y)
    {
        var colour = Enabled ? ForeColor : WizardDesign.TextDisabled;
        if (_brand is not null && text.IndexOf(_brand, StringComparison.Ordinal) is var at and >= 0)
        {
            // One run, three colours: the advances are laid out over the whole line, so the
            // product name sits exactly where it would in one colour.
            DesignText.DrawRuns(g, text, Font, 0, y, SpacingEm,
                [(0, at, colour), (at, _brand.Length, WizardDesign.Brand), (at + _brand.Length, text.Length - at - _brand.Length, colour)]);
            return;
        }

        if (_wrapWidth <= 0 && Measure(text) > Width && Width > 0)
        {
            text = DesignText.Fit(text, Font, Width, SpacingEm);
        }

        DesignText.Draw(g, text, Font, colour, 0, y, SpacingEm);
    }
}

/// <summary>
/// Text laid out the way the browser that drew the mock-ups lays it out: every glyph advances
/// by its exact, fractional width, and the positions are rounded only at the end.
/// </summary>
/// <remarks>
/// GDI rounds each glyph's advance to a whole pixel, which at 17.5 px made Inter's regular
/// text about four per cent wider than the design - a line of the intro ran 40 px longer.
/// The advances are read once per face from a 2048 px instance of the font (its design units,
/// unhinted) and scaled; GDI still draws the glyphs, with ClearType, at those positions.
/// </remarks>
internal static partial class DesignText
{
    private const float ReferenceSize = 2048;
    private static readonly Dictionary<string, Dictionary<char, float>> Advances = new(StringComparer.Ordinal);

    internal static float PixelSize(Font font, Graphics? g = null) => font.Unit == GraphicsUnit.Pixel
        ? font.Size
        : font.SizeInPoints / 72f * (g?.DpiY ?? 96f * WizardDesign.Scale);

    /// <summary>The height GDI gives a line of this font: ascent plus descent.</summary>
    internal static int FontHeight(Font font) => TextRenderer.MeasureText("Ag", font, Size.Empty, TextFormatFlags.NoPadding).Height;

    private static float Advance(Font font, char ch)
    {
        var key = font.Name;
        lock (Advances)
        {
            if (!Advances.TryGetValue(key, out var table))
            {
                Advances[key] = table = [];
            }

            if (table.TryGetValue(ch, out var known)) return known;
            var measured = MeasureReference(font, ch);
            table[ch] = measured;
            return measured;
        }
    }

    private static float MeasureReference(Font font, char ch)
    {
        using var reference = new Font(font.FontFamily, ReferenceSize, font.Style, GraphicsUnit.Pixel);
        var screen = GetDC(IntPtr.Zero);
        var hfont = reference.ToHfont();
        try
        {
            var previous = SelectObject(screen, hfont);
            var abc = new AbcFloat[1];
            var ok = GetCharABCWidthsFloatW(screen, ch, ch, abc);
            _ = SelectObject(screen, previous);
            return ok ? (abc[0].A + abc[0].B + abc[0].C) / ReferenceSize : 0.55f;
        }
        finally
        {
            _ = DeleteObject(hfont);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>
    /// Every digit takes the width of the widest one (OpenType "tnum", which GDI cannot ask a
    /// font for). Inter's digits differ in width, so a ticking "0:14" or "12.4 MB/s" moved
    /// whatever followed it by a few pixels every second.
    /// </summary>
    private static float DigitAdvance(Font font)
    {
        var widest = 0f;
        for (var digit = '0'; digit <= '9'; digit++) widest = Math.Max(widest, Advance(font, digit));
        return widest;
    }

    /// <summary>Where each glyph's cell starts and where the run ends, in device pixels from 0.</summary>
    private static float[] Positions(string text, Font font, float spacingEm, Graphics? g)
    {
        var size = PixelSize(font, g);
        var spacing = spacingEm * size;
        var digit = DigitAdvance(font);
        var positions = new float[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
        {
            var advance = char.IsAsciiDigit(text[i]) ? digit : Advance(font, text[i]);
            positions[i + 1] = positions[i] + advance * size + (i + 1 < text.Length ? spacing : 0);
        }

        return positions;
    }

    /// <summary>Where each glyph is drawn: a digit sits in the middle of its tabular cell.</summary>
    private static float[] GlyphPositions(string text, Font font, float[] positions, Graphics? g)
    {
        var size = PixelSize(font, g);
        var digit = DigitAdvance(font);
        var glyphs = (float[])positions.Clone();
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsAsciiDigit(text[i])) glyphs[i] += (digit - Advance(font, text[i])) * size / 2;
        }

        return glyphs;
    }

    internal static float Width(string text, Font font, float spacingEm = 0, Graphics? g = null) =>
        text.Length == 0 ? 0 : Positions(text, font, spacingEm, g)[^1];

    /// <summary>The text, cut with an ellipsis where it would run past <paramref name="width"/>.</summary>
    internal static string Fit(string text, Font font, float width, float spacingEm = 0)
    {
        if (Width(text, font, spacingEm) <= width) return text;
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length].TrimEnd() + "…";
            if (Width(candidate, font, spacingEm) <= width) return candidate;
        }

        return "…";
    }

    /// <summary>Draws one colour of text with its top-left at (x, y); returns where it ends.</summary>
    internal static float Draw(Graphics g, string text, Font font, Color colour, float x, int y, float spacingEm = 0) =>
        DrawRuns(g, text, font, x, y, spacingEm, [(0, text.Length, colour)]);

    /// <summary>Draws a line whose parts have different colours; the layout is that of the whole line.</summary>
    internal static float DrawRuns(Graphics g, string text, Font font, float x, int y, float spacingEm, (int Start, int Length, Color Colour)[] runs)
    {
        if (text.Length == 0) return x;
        var positions = GlyphPositions(text, font, Positions(text, font, spacingEm, g), g);
        var hdc = g.GetHdc();
        var hfont = font.ToHfont();
        try
        {
            var previous = SelectObject(hdc, hfont);
            _ = SetBkMode(hdc, 1);
            foreach (var (start, length, colour) in runs)
            {
                if (length <= 0) continue;
                var advances = new int[length];
                for (var i = 0; i < length; i++)
                {
                    advances[i] = (int)Math.Round(x + positions[start + i + 1]) - (int)Math.Round(x + positions[start + i]);
                }

                _ = SetTextColor(hdc, colour.R | colour.G << 8 | colour.B << 16);
                _ = ExtTextOutW(hdc, (int)Math.Round(x + positions[start]), y, 0, IntPtr.Zero, text.Substring(start, length), (uint)length, advances);
            }

            _ = SelectObject(hdc, previous);
        }
        finally
        {
            _ = DeleteObject(hfont);
            g.ReleaseHdc(hdc);
        }

        return x + positions[^1];
    }

    // TextRenderer-shaped helpers, so the components read as they did.

    internal static Size MeasureText(string text, Font font, Size proposedSize, TextFormatFlags flags) =>
        new((int)Math.Ceiling(Width(text, font)), FontHeight(font));

    internal static Size MeasureText(Graphics g, string text, Font font, Size proposedSize, TextFormatFlags flags) =>
        new((int)Math.Ceiling(Width(text, font, 0, g)), FontHeight(font));

    internal static void DrawText(Graphics g, string text, Font font, Point point, Color colour, TextFormatFlags flags) =>
        Draw(g, text, font, colour, point.X, point.Y);

    internal static void DrawText(Graphics g, string text, Font font, Rectangle bounds, Color colour, TextFormatFlags flags)
    {
        if ((flags & TextFormatFlags.EndEllipsis) != 0) text = Fit(text, font, bounds.Width);
        var width = Width(text, font, 0, g);
        var height = FontHeight(font);
        var x = (flags & TextFormatFlags.HorizontalCenter) != 0 ? bounds.X + (bounds.Width - width) / 2f
            : (flags & TextFormatFlags.Right) != 0 ? bounds.Right - width
            : bounds.X;
        var y = (flags & TextFormatFlags.VerticalCenter) != 0 ? bounds.Y + (bounds.Height - height) / 2
            : (flags & TextFormatFlags.Bottom) != 0 ? bounds.Bottom - height
            : bounds.Y;
        Draw(g, text, font, colour, x, y);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AbcFloat
    {
        public float A;
        public float B;
        public float C;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr window, IntPtr hdc);

    [LibraryImport("gdi32.dll", EntryPoint = "GetCharABCWidthsFloatW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCharABCWidthsFloatW(IntPtr hdc, uint first, uint last, [Out] AbcFloat[] widths);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport("gdi32.dll")]
    private static partial int SetBkMode(IntPtr hdc, int mode);

    [LibraryImport("gdi32.dll")]
    private static partial int SetTextColor(IntPtr hdc, int colour);

    [LibraryImport("gdi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect, string text, uint count, int[] advances);
}
