using System.Security.Cryptography;
using System.Text.Json;

using DexTor.Contracts;
using DexTor.SessionHost;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// NTFS valid data length: the pass must make the whole data.img written without
/// changing a byte, because the image may already hold the user's storage.
/// </summary>
public sealed class DataImageValidDataTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DataImageValidDataTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PassPreservesEveryByteAndTheLength()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "data.img");
        const long size = 20L * 1024 * 1024 + 4096; // not a multiple of the chunk size
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.SetLength(size);
            foreach (var offset in new long[] { 0, 9L * 1024 * 1024 + 7, size - 4096 })
            {
                stream.Position = offset;
                stream.Write(RandomNumberGenerator.GetBytes(4096));
            }
        }
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var updates = new List<(long Done, long Total)>();

        DataImageValidData.Extend(path, (done, total) => updates.Add((done, total)), CancellationToken.None);

        new FileInfo(path).Length.Should().Be(size);
        SHA256.HashData(File.ReadAllBytes(path)).Should().Equal(before);
        updates[0].Should().Be((0L, size));
        updates[^1].Should().Be((size, size));
    }

    [Fact]
    public void MarkAddsTheFlagAndKeepsOtherMetadata()
    {
        Directory.CreateDirectory(_root);
        var metadata = Path.Combine(_root, "device.json");
        File.WriteAllText(metadata, """{"state":"ready","format":3,"dataImageSizeBytes":4096}""");

        DataImageValidData.Mark(metadata);

        using var document = JsonDocument.Parse(File.ReadAllText(metadata));
        DataImageValidData.IsMarked(document.RootElement).Should().BeTrue();
        document.RootElement.GetProperty("state").GetString().Should().Be("ready");
        document.RootElement.GetProperty("dataImageSizeBytes").GetInt64().Should().Be(4096);
        File.Exists(metadata + ".partial").Should().BeFalse();
    }

    [Fact]
    public async Task ReadinessCheckRunsThePassOnceForOlderSticks()
    {
        var data = Path.Combine(_root, "DEXTOR", "storage", "data.img");
        var metadata = Path.Combine(_root, "DEXTOR", "metadata", "device.json");
        var system = Path.Combine(_root, "DEXTOR", "system", "slot-a", "system.img");
        foreach (var file in new[] { data, metadata, system }) Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(system, "system"u8.ToArray());
        using (var stream = new FileStream(data, FileMode.Create, FileAccess.Write, FileShare.None)) stream.SetLength(8192);
        File.WriteAllText(metadata, JsonSerializer.Serialize(new
        {
            state = "ready", format = 3,
            systemImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(system))).ToLowerInvariant(),
            systemImagePath = @"DEXTOR\system\slot-a\system.img", dataImagePath = @"DEXTOR\storage\data.img",
            dataImageSizeBytes = 8192
        }));
        var updates = new List<DexTorUsbVerificationProgress>();
        var progress = new SynchronousProgress<DexTorUsbVerificationProgress>(updates.Add);

        (await DexTorUsbReadinessVerifier.VerifyAsync(_root, progress, verifySystemImageHash: false)).IsReady.Should().BeTrue();
        updates.Should().Contain(update => update.BytesChecked == 8192 && update.TotalBytes == 8192);
        using (var document = JsonDocument.Parse(File.ReadAllText(metadata)))
        {
            DataImageValidData.IsMarked(document.RootElement).Should().BeTrue();
        }

        updates.Clear();
        (await DexTorUsbReadinessVerifier.VerifyAsync(_root, progress, verifySystemImageHash: false)).IsReady.Should().BeTrue();
        updates.Should().BeEmpty("the pass runs once");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
