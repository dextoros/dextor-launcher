using System.Text.Json;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class UsbPayloadVerifierTests
{
    [Fact]
    public void VerifiesWrittenPayloadAndOnlyThenMarksDeviceReady()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor-usb-verify", Guid.NewGuid().ToString("N"));
        try
        {
            var systemPath = Path.Combine(root, "DEXTOR", "system", "slot-a", "system.img");
            var dataPath = Path.Combine(root, "DEXTOR", "storage", "data.img");
            Directory.CreateDirectory(Path.GetDirectoryName(systemPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            File.WriteAllText(systemPath, "verified system");
            using (var data = File.Create(dataPath))
            {
                data.SetLength(1024 * 1024);
            }

            var expectedHash = IsoBootPayloadExtractor.ComputeSha256(systemPath);
            var verification = UsbPayloadVerifier.Verify(root, expectedHash, 1024 * 1024);
            UsbPayloadVerifier.MarkReady(root, Guid.NewGuid(), verification);

            verification.SystemImagePath.Should().Be(systemPath);
            verification.DataImagePath.Should().Be(dataPath);
            Directory.GetFiles(root, ".dextor-verify-*.tmp").Should().BeEmpty();
            using var metadata = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "DEXTOR", "metadata", "device.json")));
            metadata.RootElement.GetProperty("state").GetString().Should().Be("ready");
            metadata.RootElement.GetProperty("format").GetInt32().Should().Be(3);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void RejectsWrongSystemHashAndDataImageSize()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor-usb-verify", Guid.NewGuid().ToString("N"));
        try
        {
            var systemPath = Path.Combine(root, "DEXTOR", "system", "slot-a", "system.img");
            var dataPath = Path.Combine(root, "DEXTOR", "storage", "data.img");
            Directory.CreateDirectory(Path.GetDirectoryName(systemPath)!);
            Directory.CreateDirectory(Path.GetDirectoryName(dataPath)!);
            File.WriteAllText(systemPath, "system");
            File.WriteAllBytes(dataPath, new byte[16]);

            var wrongHash = () => UsbPayloadVerifier.Verify(root, new string('a', 64), 16);
            wrongHash.Should().Throw<IOException>().WithMessage("*hash*");

            var hash = IsoBootPayloadExtractor.ComputeSha256(systemPath);
            var wrongSize = () => UsbPayloadVerifier.Verify(root, hash, 32);
            wrongSize.Should().Throw<IOException>().WithMessage("*size*");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
