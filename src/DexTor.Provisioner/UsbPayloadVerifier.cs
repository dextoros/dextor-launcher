using System.Security.Cryptography;
using System.Text.Json;

namespace DexTor.Provisioner;

public sealed record UsbPayloadVerificationResult(
    string SystemImagePath,
    string DataImagePath,
    string SystemImageSha256,
    long DataImageSizeBytes);

public static class UsbPayloadVerifier
{
    public static UsbPayloadVerificationResult Verify(
        string dataRoot,
        string expectedSystemImageSha256,
        long expectedDataImageSizeBytes,
        Action<long, long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = Path.GetFullPath(dataRoot);
        var systemImagePath = Path.Combine(normalizedRoot, "DEXTOR", "system", "slot-a", "system.img");
        var dataImagePath = Path.Combine(normalizedRoot, "DEXTOR", "storage", "data.img");
        if (!File.Exists(systemImagePath))
        {
            throw new IOException("The system image is missing from the USB after writing.");
        }

        // Read from the stick, not from the Windows cache the copy may still sit in.
        var actualHash = UnbufferedFile.Sha256(systemImagePath, progress, cancellationToken);
        if (!string.Equals(actualHash, expectedSystemImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The system image hash does not match after writing to USB.");
        }

        if (!File.Exists(dataImagePath) || new FileInfo(dataImagePath).Length != expectedDataImageSizeBytes)
        {
            throw new IOException("The private data image has an unexpected size after writing to USB.");
        }

        using (var dataImage = new FileStream(
            dataImagePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            FileOptions.WriteThrough))
        {
            if (dataImage.Length > 0)
            {
                dataImage.Position = dataImage.Length - 1;
                var lastByte = dataImage.ReadByte();
                dataImage.Position = dataImage.Length - 1;
                dataImage.WriteByte(checked((byte)lastByte));
            }

            DurableFlush.Flush(dataImage);
        }

        VerifyVolumeIo(normalizedRoot);
        return new UsbPayloadVerificationResult(
            systemImagePath,
            dataImagePath,
            actualHash,
            expectedDataImageSizeBytes);
    }

    public static void MarkReady(
        string dataRoot,
        Guid requestId,
        UsbPayloadVerificationResult verification)
    {
        var metadataDirectory = Path.Combine(Path.GetFullPath(dataRoot), "DEXTOR", "metadata");
        Directory.CreateDirectory(metadataDirectory);
        var metadataPath = Path.Combine(metadataDirectory, "device.json");
        var partialPath = metadataPath + ".partial";
        var json = JsonSerializer.Serialize(new
        {
            state = "ready",
            format = 3,
            requestId,
            systemImageSha256 = verification.SystemImageSha256,
            systemImagePath = @"DEXTOR\system\slot-a\system.img",
            dataImagePath = @"DEXTOR\storage\data.img",
            dataImageSizeBytes = verification.DataImageSizeBytes,
            storageState = "storage_not_initialized",
            dataImageValidDataExtended = true,
            verifiedUtc = DateTimeOffset.UtcNow
        });
        File.WriteAllText(partialPath, json);
        using (var metadata = new FileStream(partialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            _ = JsonDocument.Parse(metadata);
        }

        File.Move(partialPath, metadataPath, true);
        using var readyMetadata = JsonDocument.Parse(File.ReadAllText(metadataPath));
        if (!string.Equals(
            readyMetadata.RootElement.GetProperty("state").GetString(),
            "ready",
            StringComparison.Ordinal))
        {
            throw new IOException("DexTor USB metadata could not be verified.");
        }
    }

    private static void VerifyVolumeIo(string dataRoot)
    {
        var probePath = Path.Combine(dataRoot, $".dextor-verify-{Guid.NewGuid():N}.tmp");
        var expected = RandomNumberGenerator.GetBytes(4096);
        try
        {
            using (var probe = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                probe.Write(expected);
                DurableFlush.Flush(probe);
                probe.Position = 0;
                var actual = new byte[expected.Length];
                probe.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(expected))
                {
                    throw new IOException("The USB data volume failed its read-back test.");
                }
            }
        }
        finally
        {
            File.Delete(probePath);
        }
    }
}
