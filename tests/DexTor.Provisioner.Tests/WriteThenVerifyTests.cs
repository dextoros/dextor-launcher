using System.Security.Cryptography;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

/// <summary>Step 5 writes everything first and checks it after, from the stick, not the cache.</summary>
public sealed class WriteThenVerifyTests
{
    private const int SectorSize = HybridGptDiskWriter.SectorSize;

    [Fact]
    public async Task TheBootImageIsWrittenWithoutReadingBackAndCheckedLaterOnItsOwnCounter()
    {
        const int isoSectors = 32 * 1024;
        const int diskSectors = 128 * 1024;
        await using var image = HybridGptDiskWriterTests.BuildHybridImage(isoSectors);
        await using var disk = new MemoryStream(new byte[diskSectors * SectorSize], writable: true);
        var written = new List<long>();

        await HybridGptDiskWriter.WriteAsync(image, disk, (long)diskSectors * SectorSize, new InlineProgress<long>(written.Add), verify: false);

        written.Max().Should().Be(image.Length, "writing reports only the bytes written, not a read-back after them");
        var compared = new List<long>();
        await HybridGptDiskWriter.VerifyAsync(image, disk, new InlineProgress<long>(compared.Add));
        compared.Should().BeInAscendingOrder();
        compared[^1].Should().Be(image.Length);
    }

    [Fact]
    public async Task ACorruptedBlockOnTheStickFailsTheLaterCheck()
    {
        const int isoSectors = 32 * 1024;
        const int diskSectors = 128 * 1024;
        await using var image = HybridGptDiskWriterTests.BuildHybridImage(isoSectors);
        var bytes = new byte[diskSectors * SectorSize];
        await using var disk = new MemoryStream(bytes, writable: true);
        await HybridGptDiskWriter.WriteAsync(image, disk, (long)diskSectors * SectorSize, verify: false);

        bytes[20 * 1024 * 1024 / 2 + 7] ^= 0xFF;

        var check = () => HybridGptDiskWriter.VerifyAsync(image, disk);
        await check.Should().ThrowAsync<IOException>();
    }

    [Theory]
    [InlineData(4 * 1024 * 1024)]
    [InlineData(5 * 1024 * 1024 + 2048)]
    [InlineData(2048)]
    public void TheCopyForWindowsBypassesTheCacheAndKeepsItsExactLength(int length)
    {
        var folder = Path.Combine(Path.GetTempPath(), "dextor-unbuffered", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var source = Path.Combine(folder, "image.iso");
            var data = new byte[length];
            new Random(5).NextBytes(data);
            File.WriteAllBytes(source, data);
            var destination = Path.Combine(folder, "system.img");
            var reported = new List<long>();

            UnbufferedFile.Copy(source, destination, reported.Add, CancellationToken.None);

            new FileInfo(destination).Length.Should().Be(length, "the padded last sector is cut off again");
            File.ReadAllBytes(destination).Should().Equal(data);
            reported[^1].Should().Be(length);
            var checkedBytes = new List<long>();
            UnbufferedFile.Sha256(destination, (done, _) => checkedBytes.Add(done))
                .Should().Be(Convert.ToHexString(SHA256.HashData(data)));
            checkedBytes[^1].Should().Be(length);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void TheUnbufferedReaderReturnsExactlyTheBytesAtAnyOffsetAndLength()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dextor-unbuffered", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "disk.bin");
            var data = new byte[9 * 1024 * 1024];
            new Random(9).NextBytes(data);
            File.WriteAllBytes(path, data);

            using var stream = new UnbufferedReadStream(File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, UnbufferedFile.NoBuffering), 1024 * 1024);
            foreach (var (offset, length) in new[] { (0, 4096), (7, 100), (4095, 3 * 1024 * 1024 + 11), (data.Length - 300, 300) })
            {
                var buffer = new byte[length];
                stream.Position = offset;
                stream.ReadExactly(buffer);
                buffer.Should().Equal(data.AsSpan(offset, length).ToArray(), $"{length} bytes at {offset}");
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }
}
