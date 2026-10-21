using System.Security.Cryptography;

using DiscUtils.Fat;
using DiscUtils.Iso9660;

namespace DexTor.Provisioner;

public sealed record IsoBootPayloadInfo(
    string VolumeLabel,
    long IsoSizeBytes,
    long LivePayloadSizeBytes,
    string KernelPath = "",
    string InitrdPath = "");

public static class IsoBootPayloadExtractor
{
    private const long Fat32MaximumFileBytes = uint.MaxValue;
    private const long BootVolumePayloadLimitBytes = 15L * 1024 * 1024 * 1024 / 4;

    /// <summary>
    /// The files the contract names by path. The kernel and the initrd are deliberately
    /// not in this list: the image names those in its own boot configuration, under the
    /// version of the kernel package it was built with, and
    /// <see cref="LiveImageBootAssets"/> reads them from there.
    /// </summary>
    private static readonly string[] RequiredIsoFiles =
    [
        "efi.img",
        "boot/grub/grub.cfg",
        "live/filesystem.squashfs"
    ];

    public static IsoBootPayloadInfo Inspect(string isoPath)
    {
        using var isoStream = File.OpenRead(isoPath);
        using var iso = new CDReader(isoStream, true);
        foreach (var required in RequiredIsoFiles)
        {
            if (!iso.FileExists(ToDiscPath(required)))
            {
                throw new LiveImageBootAssetException($"The Live ISO is missing a required boot file: {required}");
            }
        }

        var bootPair = LiveImageBootAssets.Resolve(iso);

        using var efiImage = iso.OpenFile(ToDiscPath("efi.img"), FileMode.Open);
        using var fat = new FatFileSystem(efiImage);
        if (!fat.FileExists(@"efi\boot\bootx64.efi"))
        {
            throw new LiveImageBootAssetException("The Live ISO EFI image does not contain EFI/BOOT/BOOTX64.EFI.");
        }

        var livePayload = new[]
        {
            ToDiscPath("live/filesystem.squashfs"),
            bootPair.KernelPath,
            bootPair.InitrdPath
        };
        var liveSize = livePayload.Sum(path => iso.GetFileInfo(path).Length);
        var everyBootFile = RequiredIsoFiles.Select(ToDiscPath).Concat(livePayload);
        if (everyBootFile.Select(path => iso.GetFileInfo(path).Length).Any(length => length > Fat32MaximumFileBytes))
        {
            throw new LiveImageBootAssetException("The Live ISO contains a boot file that is too large for FAT32.");
        }

        if (liveSize > BootVolumePayloadLimitBytes)
        {
            throw new LiveImageBootAssetException("The Live ISO boot payload does not fit the DexTor boot partition.");
        }

        return new IsoBootPayloadInfo(
            iso.VolumeLabel,
            isoStream.Length,
            liveSize,
            bootPair.KernelPath,
            bootPair.InitrdPath);
    }

    public static void ExtractToBootVolume(
        string isoPath,
        string destinationRoot,
        CancellationToken cancellationToken = default)
    {
        var payload = Inspect(isoPath);
        using var isoStream = File.OpenRead(isoPath);
        using var iso = new CDReader(isoStream, true);
        CopyTree(iso, @"boot", destinationRoot, cancellationToken);
        CopyTree(iso, @"live", destinationRoot, cancellationToken);

        // The boot configuration is copied verbatim, so the kernel and initrd it names
        // must be on the boot volume under exactly those names. Validating the pair and
        // then shipping a tree that does not contain it would leave a volume whose GRUB
        // references resolve to nothing.
        foreach (var required in new[] { payload.KernelPath, payload.InitrdPath })
        {
            var extracted = Path.Combine(destinationRoot, NormalizeIsoRelativePath(required));
            if (!File.Exists(extracted))
            {
                throw new LiveImageBootAssetException(
                    $"The Live ISO boot file named by the boot configuration was not extracted: /{required.Replace('\\', '/')}");
            }
        }

        using var efiImage = iso.OpenFile(ToDiscPath("efi.img"), FileMode.Open);
        using var fat = new FatFileSystem(efiImage);
        var destination = Path.Combine(destinationRoot, "EFI", "BOOT", "BOOTX64.EFI");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var source = fat.OpenFile(@"efi\boot\bootx64.efi", FileMode.Open);
        using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
        DurableFlush.Flush(target);
    }

    public static string ComputeSha256(string path) => ComputeSha256(path, null);

    /// <summary>Hashes a file, reporting bytes read and the total as it goes.</summary>
    public static string ComputeSha256(string path, Action<long, long>? progress)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var total = stream.Length;
        var buffer = new byte[4 * 1024 * 1024];
        long done = 0;
        int read;
        progress?.Invoke(0, total);
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            progress?.Invoke(done, total);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void CopyTree(
        CDReader iso,
        string sourceDirectory,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in iso.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedRoot = Path.GetFullPath(destinationRoot);
            var relativePath = NormalizeIsoRelativePath(sourcePath);
            var destination = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
            if (!destination.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The Live ISO contains an unsafe boot path.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var source = iso.OpenFile(sourcePath, FileMode.Open);
            using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static string ToDiscPath(string path) => path.Replace('/', '\\');

    private static string NormalizeIsoRelativePath(string sourcePath)
    {
        var segments = sourcePath
            .TrimStart('\\', '/')
            .Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment =>
            {
                var versionSeparator = segment.LastIndexOf(';');
                var withoutVersion = versionSeparator > 0
                    && segment[(versionSeparator + 1)..].All(char.IsDigit)
                        ? segment[..versionSeparator]
                        : segment;
                return withoutVersion.TrimEnd('.');
            });
        return Path.Combine(segments.ToArray());
    }
}
