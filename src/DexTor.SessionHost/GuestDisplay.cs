using System.Globalization;
using System.Text;
using DexTor.Qemu;

namespace DexTor.SessionHost;

/// <summary>What the guest's display shows, as far as the VM window is concerned.</summary>
public enum GuestScreen
{
    /// <summary>No screendump could be taken.</summary>
    Unknown,

    /// <summary>QEMU's own 640×480 "Display output is not active." frame.</summary>
    Placeholder,

    /// <summary>The firmware's or GRUB's 720×400 text mode: the guest is alive, nothing to show yet.</summary>
    TextMode,

    /// <summary>A graphics mode with nothing on it yet: the kernel has the display, the splash is not up.</summary>
    Blank,

    /// <summary>A graphics mode with a picture: the splash or the desktop.</summary>
    Picture
}

/// <summary>Whether the guest has put a picture on its display yet.</summary>
/// <remarks>
/// <para>
/// Until the firmware's video BIOS runs, QEMU's virtio display shows its own 640×480
/// "Display output is not active." placeholder, and testers took that frame for the VM
/// hanging.
/// </para>
/// <para>
/// Showing the window as soon as anything else appeared let SeaBIOS, "Booting from DVD/CD…"
/// and "Welcome to GRUB!" through (720×400 text mode), and then the kernel took the display
/// and the placeholder came back for about 40 s. The window now waits for a graphics mode
/// with something on it - DexTor OS's splash or desktop - while the wizard keeps saying
/// "Starting DexTor OS". Any frame but the placeholder still counts as the guest being alive,
/// which is what the firmware-hang check needs.
/// </para>
/// </remarks>
public static class GuestDisplay
{
    public const int PlaceholderWidth = 640;
    public const int PlaceholderHeight = 480;
    public const int TextModeWidth = 720;
    public const int TextModeHeight = 400;

    public static bool IsPlaceholder(int width, int height) => width == PlaceholderWidth && height == PlaceholderHeight;

    /// <summary>The guest has drawn anything at all: the firmware is running.</summary>
    public static bool IsAlive(GuestScreen screen) => screen is GuestScreen.TextMode or GuestScreen.Blank or GuestScreen.Picture;

    /// <summary>The width and height in a binary PPM header, or null when it is not one.</summary>
    public static (int Width, int Height)? ReadPpmSize(Stream stream) => ReadPpmHeader(stream) is { } header ? (header.Width, header.Height) : null;

    /// <summary>Classifies a binary PPM screendump.</summary>
    public static GuestScreen Classify(Stream stream)
    {
        if (ReadPpmHeader(stream) is not { } header) return GuestScreen.Unknown;
        if (IsPlaceholder(header.Width, header.Height)) return GuestScreen.Placeholder;
        if (header.Width == TextModeWidth && header.Height == TextModeHeight) return GuestScreen.TextMode;
        return HasPicture(stream, header.Width, header.Height, header.Maximum) ? GuestScreen.Picture : GuestScreen.Blank;
    }

    /// <summary>
    /// Whether at least one pixel in a thousand is clearly lit. A splash is a logo on black,
    /// so its share is small; a blank frame - or one with only a cursor - has none.
    /// </summary>
    private static bool HasPicture(Stream stream, int width, int height, int maximum)
    {
        var bytesPerSample = maximum > 255 ? 2 : 1;
        var rowBytes = width * 3 * bytesPerSample;
        var row = new byte[rowBytes];
        var threshold = maximum / 6;
        long lit = 0;
        long sampled = 0;
        for (var y = 0; y < height; y++)
        {
            var read = 0;
            while (read < rowBytes)
            {
                var n = stream.Read(row, read, rowBytes - read);
                if (n == 0) return lit * 1000 > sampled && sampled > 0;
                read += n;
            }

            // Every fourth row and column is plenty for a screen of a million pixels.
            if (y % 4 != 0) continue;
            for (var x = 0; x < width; x += 4)
            {
                var offset = x * 3 * bytesPerSample;
                var r = row[offset];
                var g = row[offset + bytesPerSample];
                var b = row[offset + 2 * bytesPerSample];
                var brightest = Math.Max(r, Math.Max(g, b)) * (bytesPerSample == 2 ? 257 : 1);
                sampled++;
                if (brightest > threshold) lit++;
            }
        }

        return sampled > 0 && lit * 1000 > sampled;
    }

    private static (int Width, int Height, int Maximum)? ReadPpmHeader(Stream stream)
    {
        var header = new StringBuilder();
        var fields = new List<string>();
        int value;
        // "P6", width, height, maximum - separated by whitespace, with # comments; one
        // whitespace byte after the maximum, then the pixels.
        while (fields.Count < 4 && (value = stream.ReadByte()) >= 0)
        {
            var character = (char)value;
            if (character == '#' && header.Length == 0)
            {
                while ((value = stream.ReadByte()) >= 0 && value != '\n') { }
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (header.Length > 0) fields.Add(header.ToString());
                header.Clear();
            }
            else
            {
                header.Append(character);
            }
        }

        if (fields.Count < 3 || fields[0] != "P6") return null;
        if (!int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var width)
            || !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            return null;
        }

        var maximum = fields.Count > 3 && int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var max) ? max : 255;
        return (width, height, maximum is > 0 and <= 65535 ? maximum : 255);
    }

    /// <summary>
    /// Asks QEMU for a screendump and says what it shows; <see cref="GuestScreen.Unknown"/>
    /// when the dump could not be taken, so a window is never shown on a guess.
    /// </summary>
    public static async Task<GuestScreen> ReadAsync(QmpSession qmp, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dextor-display-{Guid.NewGuid():N}.ppm");
        try
        {
            var response = await qmp.ExecuteAsync("screendump", new { filename = path }, TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(false);
            if (!response.Succeeded || !File.Exists(path)) return GuestScreen.Unknown;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            return Classify(stream);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or TimeoutException)
        {
            return GuestScreen.Unknown;
        }
        finally
        {
            try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
