using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DexTor.Launcher;

/// <summary>
/// Inter, the wizard's typeface, carried inside the launcher and loaded for this process only.
/// </summary>
/// <remarks>
/// Windows does not ship Inter. Each face is added twice: to a GDI+ collection, which gives
/// the <see cref="FontFamily"/> a <see cref="Font"/> is made from, and to GDI as a private
/// memory font, so <see cref="System.Windows.Forms.TextRenderer"/> and labels - which draw
/// through GDI and look the face up by name - find it too. Nothing is installed; when a face
/// cannot be loaded the design's fallback, Segoe UI, is used.
/// </remarks>
internal static partial class WizardFonts
{
    private static readonly PrivateFontCollection Collection = new();
    private static readonly Dictionary<int, FontFamily?> Families = Load();

    /// <summary>True when Inter was loaded; false means the Segoe UI fallback is in use.</summary>
    internal static bool InterLoaded => Families.Values.Any(family => family is not null);

    /// <summary>
    /// A font of <paramref name="pixels"/> design pixels - the design's own unit - at weight 400,
    /// 500 or 600. It is sized in points, so it grows with the display scale like the layout.
    /// </summary>
    /// <remarks>Every call returns a new font; the caller owns and may dispose it.</remarks>
    internal static Font Get(float pixels, int weight = 400)
    {
        // Device pixels of the wizard's own scale (WizardDesign.Scale), not points of the
        // display's: the scale is capped so the window fits small screens, and the text with it.
        var points = pixels * WizardDesign.Scale;
        if (Cjk)
        {
            return new Font(CjkFamilyName, points, weight >= 600 ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        }

        if (Families.TryGetValue(Normalize(weight), out var family) && family is not null)
        {
            return new Font(family, points, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        return weight >= 600
            ? new Font("Segoe UI Semibold", points, FontStyle.Regular, GraphicsUnit.Pixel)
            : new Font("Segoe UI", points, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    /// <summary>The family for outlines (<see cref="System.Drawing.Drawing2D.GraphicsPath.AddString(string, FontFamily, int, float, PointF, StringFormat)"/>).</summary>
    internal static FontFamily Family(int weight = 600) => Cjk ? new FontFamily(CjkFamilyName) :
        Families.TryGetValue(Normalize(weight), out var family) && family is not null
            ? family
            : new FontFamily(weight >= 600 ? "Segoe UI Semibold" : "Segoe UI");

    /// <summary>
    /// The face for Chinese: Inter has no CJK glyphs, and the ones Windows substituted were
    /// placed at about half their width, so neighbours ran into each other. Microsoft YaHei UI
    /// ships with every Windows the launcher runs on and has Latin letters of its own.
    /// </summary>
    internal const string CjkFamilyName = "Microsoft YaHei UI";

    /// <summary>Set while the wizard speaks a CJK language; every font made then is <see cref="CjkFamilyName"/>.</summary>
    internal static bool Cjk { get; private set; }

    /// <summary>Picks the face for <paramref name="language"/>; true when that changed it.</summary>
    internal static bool UseLanguage(string? language)
    {
        var cjk = language is not null && (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
            || language.StartsWith("ko", StringComparison.OrdinalIgnoreCase));
        if (cjk == Cjk) return false;
        Cjk = cjk;
        return true;
    }

    /// <summary>
    /// The same size and weight as <paramref name="font"/> in the face now in use, or null when
    /// <paramref name="font"/> is not one of the wizard's own.
    /// </summary>
    internal static Font? Restyle(Font font)
    {
        var name = font.FontFamily.Name;
        int weight;
        if (name == CjkFamilyName) weight = font.Bold ? 600 : 400;
        else if (name.StartsWith("Inter", StringComparison.Ordinal)) weight = name.EndsWith("SemiBold", StringComparison.Ordinal) ? 600 : name.EndsWith("Medium", StringComparison.Ordinal) ? 500 : 400;
        else if (name.StartsWith("Segoe UI", StringComparison.Ordinal)) weight = name.EndsWith("Semibold", StringComparison.Ordinal) ? 600 : 400;
        else return null;
        var designPixels = font.Unit == GraphicsUnit.Pixel ? font.Size / WizardDesign.Scale : font.SizeInPoints / 0.75f;
        return Get(designPixels, weight);
    }

    private static int Normalize(int weight) => weight >= 600 ? 600 : weight >= 500 ? 500 : 400;

    private static Dictionary<int, FontFamily?> Load()
    {
        var faces = new Dictionary<int, FontFamily?>();
        foreach (var (weight, file, name) in new[]
                 {
                     (400, "Inter-Regular.ttf", "Inter"),
                     (500, "Inter-Medium.ttf", "Inter Medium"),
                     (600, "Inter-SemiBold.ttf", "Inter SemiBold")
                 })
        {
            faces[weight] = TryAdd(file, name);
        }

        return faces;
    }

    private static FontFamily? TryAdd(string file, string name)
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("DexTor.Launcher.Assets.Fonts." + file);
            if (stream is null) return null;
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            // Both GDI+ and GDI keep reading the memory for as long as the font is in use,
            // so it is never freed: three faces, loaded once per process.
            var memory = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Collection.AddMemoryFont(memory, bytes.Length);
            uint installed = 0;
            _ = AddFontMemResourceEx(memory, (uint)bytes.Length, IntPtr.Zero, ref installed);
            return Collection.Families.FirstOrDefault(family => string.Equals(family.Name, name, StringComparison.Ordinal));
        }
        catch (Exception exception) when (exception is IOException or ExternalException or ArgumentException or OutOfMemoryException)
        {
            return null;
        }
    }

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr AddFontMemResourceEx(IntPtr font, uint length, IntPtr reserved, ref uint installed);
}
