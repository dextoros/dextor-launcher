using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Contracts;

namespace DexTor.SessionHost;

public sealed record DexTorUsbVerificationProgress(long BytesChecked, long TotalBytes);

public sealed record DexTorUsbReadinessResult(
    bool IsReady,
    string? ErrorMessage = null,
    string? SystemImagePath = null,
    string? DataImagePath = null);

public static class DexTorUsbReadinessVerifier
{
    private const int CurrentMetadataFormat = 3;

    public static async Task<DexTorUsbReadinessResult> VerifyAsync(
        string usbRootPath,
        IProgress<DexTorUsbVerificationProgress>? progress = null,
        bool verifySystemImageHash = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = Path.GetFullPath(usbRootPath);
            var metadataPath = Path.Combine(root, "DEXTOR", "metadata", "device.json");
            if (!File.Exists(metadataPath)) return NotReady("DexTor USB metadata is missing.");

            // Read into memory: the one-time data image pass below rewrites device.json.
            var metadataBytes = await File.ReadAllBytesAsync(metadataPath, cancellationToken);
            using var document = JsonDocument.Parse(metadataBytes);
            var metadata = document.RootElement;
            if (!TryGetString(metadata, "state", out var state)
                || !string.Equals(state, "ready", StringComparison.Ordinal))
            {
                return NotReady("DexTor USB has not passed final verification.");
            }

            if (!metadata.TryGetProperty("format", out var formatElement)
                || !formatElement.TryGetInt32(out var format)
                || format != CurrentMetadataFormat)
            {
                return NotReady("DexTor USB metadata format is not supported.");
            }

            if (!TryGetString(metadata, "systemImageSha256", out var expectedHash)
                || expectedHash.Length != 64
                || !expectedHash.All(Uri.IsHexDigit))
            {
                return NotReady("DexTor USB system image checksum is invalid.");
            }

            if (!TryGetString(metadata, "systemImagePath", out var systemRelativePath)
                || !TryResolveContainedPath(root, systemRelativePath, out var systemImagePath)
                || !TryGetString(metadata, "dataImagePath", out var dataRelativePath)
                || !TryResolveContainedPath(root, dataRelativePath, out var dataImagePath))
            {
                return NotReady("DexTor USB metadata contains an invalid payload path.");
            }

            if (!File.Exists(systemImagePath)) return NotReady("DexTor USB system image is missing.");
            if (!File.Exists(dataImagePath)) return NotReady("DexTor USB private data image is missing.");
            if (!metadata.TryGetProperty("dataImageSizeBytes", out var sizeElement)
                || !sizeElement.TryGetInt64(out var expectedDataSize)
                || expectedDataSize <= 0
                || new FileInfo(dataImagePath).Length != expectedDataSize)
            {
                return NotReady("DexTor USB private data image has an unexpected size.");
            }

            using (new FileStream(dataImagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            if (!DataImageValidData.IsMarked(metadata))
            {
                // Once, for sticks created before the launcher wrote the whole
                // image: without it every new write offset in the VM made NTFS
                // zero-fill gigabytes first. The pass never changes a byte.
                await Task.Run(() => DataImageValidData.Extend(
                    dataImagePath,
                    (done, total) => progress?.Report(new DexTorUsbVerificationProgress(done, total)),
                    cancellationToken), cancellationToken);
                DataImageValidData.Mark(metadataPath);
            }

            if (verifySystemImageHash)
            {
                var actualHash = await ComputeSha256Async(systemImagePath, progress, cancellationToken);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return NotReady("DexTor USB system image checksum does not match.");
                }
            }

            return new DexTorUsbReadinessResult(true, null, systemImagePath, dataImagePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or JsonException
                                          or InvalidOperationException
                                          or ArgumentException)
        {
            return NotReady(exception.Message);
        }
    }

    private static async Task<string> ComputeSha256Async(
        string path,
        IProgress<DexTorUsbVerificationProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long checkedBytes = 0;
        progress?.Report(new(0, stream.Length));
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            hash.AppendData(buffer, 0, count);
            checkedBytes += count;
            progress?.Report(new(checkedBytes, stream.Length));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryResolveContainedPath(string root, string relativePath, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return false;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        resolvedPath = Path.GetFullPath(Path.Combine(root, relativePath));
        return resolvedPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static DexTorUsbReadinessResult NotReady(string message) => new(false, message);
}
