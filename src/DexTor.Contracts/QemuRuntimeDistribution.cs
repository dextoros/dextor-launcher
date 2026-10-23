using System.Text.Json;

namespace DexTor.Contracts;

/// <summary>The committed download policy shared by portable and installer builds.</summary>
/// <remarks>
/// <see cref="InstallerUrls"/> is the order the installer is tried in: DexTor's own mirror
/// (<c>sourceUrl</c>) first, then <c>fallbackSourceUrls</c>. Every URL must serve the same
/// file; <see cref="InstallerSha512"/> is what makes any of them trustworthy, not the host.
/// </remarks>
public sealed record QemuRuntimeDistribution(string Version, string InstallerSha512, IReadOnlyList<string> InstallerUrls)
{
    public static QemuRuntimeDistribution Default { get; } = ReadEmbedded();

    public static QemuRuntimeDistribution Parse(string manifestJson)
    {
        try
        {
            using var document = JsonDocument.Parse(manifestJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || ReadString(root, "schemaVersion") != "1.0")
                throw new InvalidDataException("Unsupported QEMU manifest schema.");
            if (!root.TryGetProperty("runtime", out var runtime) || runtime.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The QEMU manifest has no runtime section.");
            var version = ReadString(runtime, "version");
            var hash = ReadString(runtime, "installerSha512");
            if (!System.Version.TryParse(version, out var parsedVersion) || parsedVersion.Build < 0)
                throw new InvalidDataException("The QEMU version must have at least three numeric components.");
            if (hash is null || hash.Length != 128 || !hash.All(char.IsAsciiHexDigit))
                throw new InvalidDataException("The QEMU installer requires a SHA-512 hash.");
            var urls = new List<string>();
            Add(ReadString(runtime, "sourceUrl"));
            if (runtime.TryGetProperty("fallbackSourceUrls", out var fallbacks))
            {
                if (fallbacks.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("fallbackSourceUrls must be an array of URLs.");
                foreach (var fallback in fallbacks.EnumerateArray())
                    Add(fallback.ValueKind == JsonValueKind.String ? fallback.GetString() : null);
            }
            return new QemuRuntimeDistribution(version!, hash.ToLowerInvariant(), urls.AsReadOnly());

            void Add(string? value)
            {
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                    throw new InvalidDataException("QEMU download URLs must use HTTPS without credentials or fragments.");
                if (!urls.Contains(uri.AbsoluteUri, StringComparer.Ordinal)) urls.Add(uri.AbsoluteUri);
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The QEMU manifest is not valid JSON.", exception);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static QemuRuntimeDistribution ReadEmbedded()
    {
        using var stream = typeof(QemuRuntimeDistribution).Assembly.GetManifestResourceStream("DexTor.Contracts.qemu-manifest.json")
            ?? throw new InvalidDataException("The embedded QEMU manifest is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
