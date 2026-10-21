using System.Globalization;

using DiscUtils.Iso9660;

namespace DexTor.Provisioner;

/// <summary>
/// The Live ISO does not carry the boot assets the provisioning contract needs, or does
/// not say unambiguously which ones they are.
/// </summary>
/// <remarks>
/// Distinct from an ordinary provisioning failure because it is a property of the image,
/// not of the USB: retrying on another drive cannot fix it, and the operator has to be
/// told that rather than sent to find a new stick.
/// </remarks>
public sealed class LiveImageBootAssetException(string message) : InvalidOperationException(message);

/// <summary>The kernel and initrd the image's own bootloader loads.</summary>
public sealed record LiveBootPair(string KernelPath, string InitrdPath);

/// <summary>
/// Reads the Live ISO's GRUB configuration to establish which kernel and initrd the image
/// actually intends to boot.
/// </summary>
/// <remarks>
/// The names are not assumed. A Debian live image built by live-build carries its kernel
/// and initrd under their package versions — <c>live/vmlinuz-6.12.107+deb13-amd64</c> and
/// the matching <c>initrd.img-…</c> — and its <c>boot/grub/grub.cfg</c> is what ties the
/// two together. Hard-coding <c>live/vmlinuz</c> described one possible spelling of the
/// contract rather than the contract itself, and rejected images that boot perfectly.
/// <para>
/// What is still required is that the image says one thing. Every menu entry that declares
/// a kernel and an initrd must name the same pair; two different pairs are refused rather
/// than resolved by preferring one, and a wildcard match is never used to find "some
/// kernel" that would make validation pass.
/// </para>
/// <para>
/// Resolution stays inside the ISO. A path that leaves the image, names a device such as
/// <c>(hd0)</c>, or sits outside the live payload directory is refused; included
/// configurations are followed only through paths that resolve inside the image, with a
/// visited set so a configuration that sources itself terminates instead of looping.
/// </para>
/// </remarks>
internal static class LiveImageBootAssets
{
    internal const string GrubConfigPath = @"boot\grub\grub.cfg";
    private const string LivePayloadDirectory = @"live\";
    private const int MaximumConfigurationFiles = 16;

    /// <summary>Resolves the one kernel/initrd pair, or explains why there is none.</summary>
    public static LiveBootPair Resolve(CDReader iso)
    {
        var pairs = new List<LiveBootPair>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadConfiguration(iso, GrubConfigPath, pairs, visited);

        var distinct = pairs
            .Distinct()
            .OrderBy(pair => pair.KernelPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (distinct.Length == 0)
        {
            throw new LiveImageBootAssetException(
                "The Live ISO boot configuration does not name a kernel and an initrd to boot.");
        }

        if (distinct.Length > 1)
        {
            var described = string.Join(
                ", ",
                distinct.Select(pair => $"{Display(pair.KernelPath)} + {Display(pair.InitrdPath)}"));
            throw new LiveImageBootAssetException(
                $"The Live ISO boot configuration names more than one kernel and initrd pair ({described}), "
                + "so DexTor will not choose between them.");
        }

        var resolved = distinct[0];
        RequirePayloadFile(iso, resolved.KernelPath, "kernel");
        RequirePayloadFile(iso, resolved.InitrdPath, "initrd");
        return resolved;
    }

    private static void ReadConfiguration(
        CDReader iso,
        string configPath,
        List<LiveBootPair> pairs,
        HashSet<string> visited)
    {
        if (!visited.Add(configPath))
        {
            // Already read. A configuration that sources itself, directly or through
            // another file, stops here instead of recursing.
            return;
        }

        if (visited.Count > MaximumConfigurationFiles)
        {
            throw new LiveImageBootAssetException(
                "The Live ISO boot configuration includes too many files to resolve.");
        }

        if (!iso.FileExists(configPath))
        {
            throw new LiveImageBootAssetException(
                $"The Live ISO is missing a boot configuration file: {Display(configPath)}");
        }

        string[] lines;
        using (var stream = iso.OpenFile(configPath, FileMode.Open))
        using (var reader = new StreamReader(stream))
        {
            lines = reader.ReadToEnd().ReplaceLineEndings("\n").Split('\n');
        }

        var directory = DirectoryOf(configPath);
        string? kernel = null;
        string? initrd = null;
        var inEntry = false;
        var entryDepth = 0;
        var depth = 0;

        foreach (var raw in lines)
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var tokens = Tokenize(line);
            var keyword = tokens.Length > 0 ? tokens[0] : string.Empty;

            if (!inEntry && keyword.Equals("menuentry", StringComparison.OrdinalIgnoreCase))
            {
                inEntry = true;
                entryDepth = depth;
                kernel = null;
                initrd = null;
            }
            else if (inEntry && tokens.Length > 1)
            {
                switch (keyword.ToLowerInvariant())
                {
                    case "linux":
                    case "linux16":
                    case "linuxefi":
                        kernel ??= Resolve(tokens[1], directory);
                        break;
                    case "initrd":
                    case "initrd16":
                    case "initrdefi":
                        initrd ??= Resolve(tokens[1], directory);
                        break;
                    default:
                        break;
                }
            }

            if (tokens.Length > 1
                && (keyword.Equals("source", StringComparison.OrdinalIgnoreCase)
                    || keyword.Equals("configfile", StringComparison.OrdinalIgnoreCase)))
            {
                ReadConfiguration(iso, Resolve(tokens[1], directory), pairs, visited);
            }

            depth += line.Count(character => character == '{') - line.Count(character => character == '}');
            if (inEntry && depth <= entryDepth)
            {
                // An entry that declares only one of the two is not a live boot pair; a
                // memory test or a firmware entry contributes nothing rather than half.
                if (kernel is not null && initrd is not null)
                {
                    pairs.Add(new LiveBootPair(kernel, initrd));
                }

                inEntry = false;
                kernel = null;
                initrd = null;
            }
        }
    }

    /// <summary>
    /// Turns a path written in the boot configuration into a path inside this image.
    /// </summary>
    private static string Resolve(string token, string configDirectory)
    {
        var value = token.Trim().Trim('"', '\'');
        if (value.Length == 0)
        {
            throw new LiveImageBootAssetException("The Live ISO boot configuration names an empty path.");
        }

        if (value.StartsWith('(') || value.Contains(':', StringComparison.Ordinal))
        {
            // A device or protocol reference points outside this image by construction.
            throw new LiveImageBootAssetException(
                $"The Live ISO boot configuration names a path outside the image: {value}");
        }

        var normalized = value.Replace('/', '\\');
        var rooted = normalized.StartsWith('\\');
        var segments = new List<string>();
        if (!rooted && configDirectory.Length > 0)
        {
            segments.AddRange(configDirectory.Split('\\', StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var segment in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new LiveImageBootAssetException(
                        $"The Live ISO boot configuration names a path that leaves the image: {value}");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
        {
            throw new LiveImageBootAssetException(
                $"The Live ISO boot configuration names a path that leaves the image: {value}");
        }

        return string.Join('\\', segments);
    }

    private static void RequirePayloadFile(CDReader iso, string path, string role)
    {
        if (!path.StartsWith(LivePayloadDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new LiveImageBootAssetException(
                $"The Live ISO boot {role} is outside the live payload directory: {Display(path)}");
        }

        if (!iso.FileExists(path))
        {
            throw new LiveImageBootAssetException(
                $"The Live ISO boot configuration names a {role} the image does not contain: {Display(path)}");
        }

        if (iso.GetFileInfo(path).Length <= 0)
        {
            throw new LiveImageBootAssetException(
                $"The Live ISO boot {role} is empty: {Display(path)}");
        }
    }

    private static string DirectoryOf(string path)
    {
        var separator = path.LastIndexOf('\\');
        return separator <= 0 ? string.Empty : path[..separator];
    }

    private static string StripComment(string line)
    {
        var comment = line.IndexOf('#', StringComparison.Ordinal);
        return comment < 0 ? line : line[..comment];
    }

    private static string[] Tokenize(string line) =>
        line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);

    private static string Display(string discPath) =>
        string.Create(CultureInfo.InvariantCulture, $"/{discPath.Replace('\\', '/')}");
}
