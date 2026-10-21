using System.Security.Cryptography;
using System.Text.Json;

using DexTor.SessionHost;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class DexTorUsbReadinessVerifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DexTorUsbReadinessVerifierTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadyPayloadWithMatchingHashAndStorageSizePasses()
    {
        var system = CreatePayload(dataSize: 4096);
        var updates = new List<DexTorUsbVerificationProgress>();

        var result = await DexTorUsbReadinessVerifier.VerifyAsync(
            _root,
            new SynchronousProgress<DexTorUsbVerificationProgress>(updates.Add));

        result.IsReady.Should().BeTrue();
        result.SystemImagePath.Should().Be(system);
        updates.Should().NotBeEmpty();
        updates[^1].BytesChecked.Should().Be(updates[^1].TotalBytes);
    }

    [Fact]
    public async Task ChangedSystemImageIsRejected()
    {
        var system = CreatePayload(dataSize: 4096);
        await File.AppendAllTextAsync(system, "changed");

        var result = await DexTorUsbReadinessVerifier.VerifyAsync(_root);

        result.IsReady.Should().BeFalse();
        result.ErrorMessage.Should().Contain("checksum");
    }

    [Fact]
    public async Task StartupQuickCheckTrustsPreviouslyVerifiedReadyMarkerWithoutRereadingImage()
    {
        var system = CreatePayload(dataSize: 4096);
        await File.AppendAllTextAsync(system, "changed");
        var updates = new List<DexTorUsbVerificationProgress>();

        var result = await DexTorUsbReadinessVerifier.VerifyAsync(
            _root,
            new SynchronousProgress<DexTorUsbVerificationProgress>(updates.Add),
            verifySystemImageHash: false);

        result.IsReady.Should().BeTrue();
        updates.Should().BeEmpty();
    }

    [Fact]
    public async Task TraversalPathAndWrongStorageSizeAreRejected()
    {
        CreatePayload(dataSize: 4096, systemPath: @"..\outside.img");

        var traversal = await DexTorUsbReadinessVerifier.VerifyAsync(_root);

        traversal.IsReady.Should().BeFalse();
        traversal.ErrorMessage.Should().Contain("path");

        CreatePayload(dataSize: 4097);
        var dataPath = Path.Combine(_root, "DEXTOR", "storage", "data.img");
        using (var data = new FileStream(dataPath, FileMode.Open, FileAccess.Write, FileShare.None)) data.SetLength(4096);

        var wrongSize = await DexTorUsbReadinessVerifier.VerifyAsync(_root);
        wrongSize.IsReady.Should().BeFalse();
        wrongSize.ErrorMessage.Should().Contain("size");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string CreatePayload(long dataSize, string systemPath = @"DEXTOR\system\slot-a\system.img")
    {
        var system = Path.Combine(_root, "DEXTOR", "system", "slot-a", "system.img");
        var data = Path.Combine(_root, "DEXTOR", "storage", "data.img");
        var metadata = Path.Combine(_root, "DEXTOR", "metadata", "device.json");
        Directory.CreateDirectory(Path.GetDirectoryName(system)!);
        Directory.CreateDirectory(Path.GetDirectoryName(data)!);
        Directory.CreateDirectory(Path.GetDirectoryName(metadata)!);
        File.WriteAllBytes(system, "DexTor test system image"u8.ToArray());
        using (var dataStream = new FileStream(data, FileMode.Create, FileAccess.Write, FileShare.None)) dataStream.SetLength(dataSize);
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(system))).ToLowerInvariant();
        File.WriteAllText(metadata, JsonSerializer.Serialize(new
        {
            state = "ready",
            format = 3,
            systemImageSha256 = hash,
            systemImagePath = systemPath,
            dataImagePath = @"DEXTOR\storage\data.img",
            dataImageSizeBytes = dataSize,
            // Sticks created by the current provisioner (see DataImageValidDataTests
            // for the one-time pass on older sticks without it).
            dataImageValidDataExtended = true
        }));
        return system;
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
