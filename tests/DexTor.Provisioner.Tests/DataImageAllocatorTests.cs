using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class DataImageAllocatorTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void TheImageGetsItsFullSizeWithoutBeingWritten()
    {
        var path = TempFile();
        try
        {
            const long length = 4L * 1024 * MiB;
            var reports = new List<(long Done, long Total)>();

            var sparse = DataImageAllocator.Create(path, length, (done, total) => reports.Add((done, total)), CancellationToken.None);

            sparse.Should().BeTrue("the test folder is on NTFS");
            new FileInfo(path).Length.Should().Be(length);
            DataImageAllocator.AllocatedBytes(path).Should().BeLessThan(MiB, "4 GB are not written to the stick");
            reports.Should().BeEmpty("nothing was written, so no write speed may be read into it");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EveryPartOfANewImageReadsAsZerosAsSecureStorageRequires()
    {
        // DexTor OS calls a disk new only when its edges and 64 probes across it read zero.
        var path = TempFile();
        try
        {
            const long length = 2L * 1024 * MiB;
            DataImageAllocator.Create(path, length, null, CancellationToken.None);

            using var image = File.OpenRead(path);
            var probe = new byte[64 * 1024];
            for (var index = 0; index < 64; index++)
            {
                image.Position = (length - probe.Length) / 63 * index;
                image.ReadExactly(probe);
                probe.Should().OnlyContain(value => value == 0, $"probe {index}");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWriteDeepInsideAllocatesOnlyWhereItLands()
    {
        // A plain file made NTFS zero-fill everything below the write first: minutes on a stick.
        var path = TempFile();
        try
        {
            const long length = 8L * 1024 * MiB;
            DataImageAllocator.Create(path, length, null, CancellationToken.None);

            using (var image = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                image.Position = 6L * 1024 * MiB;
                image.Write(new byte[4096].Select(_ => (byte)7).ToArray());
                image.Flush(true);
            }

            DataImageAllocator.AllocatedBytes(path).Should().BeLessThan(MiB);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void WhereTheVolumeCannotMakeItSparseTheWholeImageIsWrittenOnceAsBefore()
    {
        var path = TempFile();
        try
        {
            const long length = 24 * MiB;
            var reports = new List<(long Done, long Total)>();

            var sparse = DataImageAllocator.Create(path, length, (done, total) => reports.Add((done, total)), CancellationToken.None,
                _ => false);

            sparse.Should().BeFalse();
            new FileInfo(path).Length.Should().Be(length);
            reports.Should().HaveCountGreaterThan(1, "the full pass reports as it goes");
            reports[^1].Should().Be((length, length));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dextor-data-image", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "data.img");
    }
}
