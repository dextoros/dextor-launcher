using System.Text.Json;
using System.Text.Json.Nodes;

namespace DexTor.Contracts;

/// <summary>
/// Makes the whole private <c>data.img</c> "valid data" on NTFS.
/// </summary>
/// <remarks>
/// A file sized with SetLength has a valid data length of 0, and the first write
/// at offset X makes Windows zero-fill everything below X first: 61 s for one
/// 4 KiB write at 1.5 GiB on a USB stick (measured). The VM writes all over
/// data.img (ext4 backup superblocks, later file data), so the guest saw
/// minutes-long stalls and storage creation failed. One sequential pass that
/// writes every block back unchanged (reads past the valid length return zeros)
/// makes the whole file valid without changing a byte, so it is also safe for an
/// image that already holds the user's encrypted storage.
/// </remarks>
public static class DataImageValidData
{
    public const string MetadataProperty = "dataImageValidDataExtended";
    private const int ChunkBytes = 8 * 1024 * 1024;

    public static void Extend(string path, Action<long, long>? progress, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1,
            FileOptions.SequentialScan);
        var length = stream.Length;
        var buffer = new byte[ChunkBytes];
        long done = 0;
        progress?.Invoke(0, length);
        while (done < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - done);
            stream.Position = done;
            var read = 0;
            while (read < count)
            {
                var n = stream.Read(buffer, read, count - read);
                if (n == 0) throw new IOException("The private data image ended unexpectedly.");
                read += n;
            }

            stream.Position = done;
            stream.Write(buffer, 0, count);
            done += count;
            progress?.Invoke(done, length);
        }

        stream.Flush(true);
        if (stream.Length != length) throw new IOException("The private data image changed size.");
    }

    public static bool IsMarked(JsonElement metadata) =>
        metadata.TryGetProperty(MetadataProperty, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Atomically records in device.json that the pass completed.</summary>
    public static void Mark(string metadataPath)
    {
        var node = JsonNode.Parse(File.ReadAllText(metadataPath))?.AsObject()
            ?? throw new IOException("DexTor USB metadata is invalid.");
        node[MetadataProperty] = true;
        var partialPath = metadataPath + ".partial";
        File.WriteAllText(partialPath, node.ToJsonString());
        using (var partial = new FileStream(partialPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            partial.Flush(true);
        }

        File.Move(partialPath, metadataPath, true);
    }
}
