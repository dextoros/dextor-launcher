using System.Buffers.Binary;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class HybridGptDiskWriterTests
{
    private const int SectorSize = HybridGptDiskWriter.SectorSize;

    [Fact]
    public async Task PreservesHybridBootCodeAndAppendsValidDataPartition()
    {
        const int isoSectors = 32 * 1024;
        const int diskSectors = 128 * 1024;
        await using var image = BuildHybridImage(isoSectors);
        await using var disk = new MemoryStream(new byte[diskSectors * SectorSize], writable: true);
        var originalBootCode = image.ToArray().AsSpan(0, 440).ToArray();

        var layout = await HybridGptDiskWriter.WriteAsync(
            image,
            disk,
            (long)diskSectors * SectorSize);

        layout.DataPartitionEntryIndex.Should().Be(5);
        layout.DataPartitionFirstLba.Should().Be((ulong)isoSectors);
        layout.BackupHeaderLba.Should().Be((ulong)diskSectors - 1);
        var diskBytes = disk.ToArray();
        diskBytes.AsSpan(0, 440).SequenceEqual(originalBootCode).Should().BeTrue();

        var mbr = diskBytes.AsSpan(0, SectorSize);
        BinaryPrimitives.ReadUInt32LittleEndian(mbr.Slice(458, 4)).Should().Be((uint)diskSectors - 1);

        var header = diskBytes.AsSpan(SectorSize, SectorSize);
        header[..8].SequenceEqual("EFI PART"u8).Should().BeTrue();
        BinaryPrimitives.ReadUInt64LittleEndian(header.Slice(32, 8)).Should().Be((ulong)diskSectors - 1);
        HeaderCrcIsValid(header).Should().BeTrue();

        var fifthEntry = diskBytes.AsSpan(2 * SectorSize + 4 * 128, 128);
        new Guid(fifthEntry[..16]).Should().Be(HybridGptDiskWriter.MicrosoftBasicDataPartitionType);
        BinaryPrimitives.ReadUInt64LittleEndian(fifthEntry.Slice(32, 8)).Should().Be(layout.DataPartitionFirstLba);
        BinaryPrimitives.ReadUInt64LittleEndian(fifthEntry.Slice(40, 8)).Should().Be(layout.DataPartitionLastLba);
        var originalBasicDataEntry = diskBytes.AsSpan(2 * SectorSize, 128);
        BinaryPrimitives.ReadUInt64LittleEndian(originalBasicDataEntry.Slice(48, 8))
            .Should().Be(HybridGptDiskWriter.ImagePartitionHidingAttributes, "the image's own data partition is hidden and letterless");
        BinaryPrimitives.ReadUInt64LittleEndian(fifthEntry.Slice(48, 8))
            .Should().Be(HybridGptDiskWriter.NoDefaultDriveLetterAttribute);

        var backupHeader = diskBytes.AsSpan((diskSectors - 1) * SectorSize, SectorSize);
        backupHeader[..8].SequenceEqual("EFI PART"u8).Should().BeTrue();
        BinaryPrimitives.ReadUInt64LittleEndian(backupHeader.Slice(24, 8)).Should().Be((ulong)diskSectors - 1);
        BinaryPrimitives.ReadUInt64LittleEndian(backupHeader.Slice(32, 8)).Should().Be(1);
        HeaderCrcIsValid(backupHeader).Should().BeTrue();
    }

    [Fact]
    public async Task ThePartitionTableIsWrittenLastAndAlreadyHidesTheImagePartitions()
    {
        const int isoSectors = 32 * 1024;
        const int diskSectors = 128 * 1024;
        await using var image = BuildHybridImage(isoSectors);
        var disk = new WriteRecorder(new byte[diskSectors * SectorSize]);

        await HybridGptDiskWriter.WriteAsync(image, disk, (long)diskSectors * SectorSize);

        // Windows mounts what a partition table describes as soon as it sees one. With the
        // table written last, nothing inside the new partitions is written after that.
        var tableWrite = disk.Writes.FindLastIndex(write => write.Offset == 0);
        tableWrite.Should().BeGreaterThan(0);
        disk.Writes.Take(tableWrite).Should().OnlyContain(write => write.Offset >= 2 * SectorSize + 128 * 128,
            "nothing reaches the head of the disk before the table");
        disk.Writes.Skip(tableWrite + 1).Should().BeEmpty("the table closes the write");

        var head = disk.Writes[tableWrite].Bytes;
        var header = head.AsSpan(SectorSize, SectorSize);
        HeaderCrcIsValid(header).Should().BeTrue();
        var entries = head.AsSpan(2 * SectorSize, 128 * 128);
        HybridGptDiskWriter.ComputeCrc32(entries).Should().Be(BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(88, 4)));
        BinaryPrimitives.ReadUInt64LittleEndian(entries.Slice(48, 8))
            .Should().Be(HybridGptDiskWriter.ImagePartitionHidingAttributes);
        BinaryPrimitives.ReadUInt64LittleEndian(entries.Slice(128 + 48, 8))
            .Should().Be(0, "only basic-data partitions carry the Microsoft attributes");
    }

    private sealed class WriteRecorder(byte[] buffer) : MemoryStream(buffer, writable: true)
    {
        public List<(long Offset, byte[] Bytes)> Writes { get; } = [];

        // WriteAsync lands in one of the synchronous overloads below, so it is not recorded twice.
        public override void Write(byte[] source, int offset, int count)
        {
            Writes.Add((Position, source.AsSpan(offset, count).ToArray()));
            base.Write(source, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> source)
        {
            Writes.Add((Position, source.ToArray()));
            base.Write(source);
        }
    }
    [Fact]
    public async Task WritingReportsBytesForTheCopyAndTheReadBackInOrder()
    {
        const int isoSectors = 32 * 1024;
        const int diskSectors = 128 * 1024;
        await using var image = BuildHybridImage(isoSectors);
        await using var disk = new MemoryStream(new byte[diskSectors * SectorSize], writable: true);
        var reports = new List<long>();

        await HybridGptDiskWriter.WriteAsync(image, disk, (long)diskSectors * SectorSize, new InlineProgress<long>(reports.Add));

        reports.Should().NotBeEmpty();
        reports.Should().BeInAscendingOrder();
        reports[^1].Should().Be(2L * isoSectors * SectorSize, "written once and read back once");
    }

    [Fact]
    public void ByteReportsAreThrottledButTheLastOfAPassAlwaysGetsThrough()
    {
        var throttle = new ProvisionerCommand.ByteProgressThrottle(TimeSpan.FromHours(1));

        throttle.ShouldReport(10, 100).Should().BeTrue("the first report of a pass is shown at once");
        throttle.ShouldReport(20, 100).Should().BeFalse();
        throttle.ShouldReport(100, 100).Should().BeTrue();
    }
    [Fact]
    public async Task RejectsImageWithoutHybridMbrBeforeWritingDestination()
    {
        await using var image = new MemoryStream(new byte[4 * 1024 * 1024], writable: false);
        await using var disk = new MemoryStream(new byte[16 * 1024 * 1024], writable: true);

        var action = () => HybridGptDiskWriter.WriteAsync(image, disk, disk.Length);

        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bootable MBR*");
        disk.ToArray().Should().OnlyContain(value => value == 0);
    }

    internal static MemoryStream BuildHybridImage(int sectorCount)
    {
        const int entryCount = 128;
        const int entrySize = 128;
        const int entriesSectors = entryCount * entrySize / SectorSize;
        var bytes = new byte[sectorCount * SectorSize];
        bytes[0] = 0xEB;
        bytes[1] = 0x3C;
        bytes[510] = 0x55;
        bytes[511] = 0xAA;
        bytes[446 + 4] = 0xEE;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(446 + 8, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(446 + 12, 4), (uint)sectorCount - 1);

        var entries = bytes.AsSpan(2 * SectorSize, entryCount * entrySize);
        for (var index = 0; index < 4; index++)
        {
            (index == 0 ? HybridGptDiskWriter.MicrosoftBasicDataPartitionType : Guid.NewGuid())
                .TryWriteBytes(entries.Slice(index * entrySize, 16));
            Guid.NewGuid().TryWriteBytes(entries.Slice(index * entrySize + 16, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(entries.Slice(index * entrySize + 32, 8), (ulong)(2048 + index * 2048));
            BinaryPrimitives.WriteUInt64LittleEndian(entries.Slice(index * entrySize + 40, 8), (ulong)(4095 + index * 2048));
        }

        var entriesCrc = HybridGptDiskWriter.ComputeCrc32(entries);
        var header = bytes.AsSpan(SectorSize, SectorSize);
        "EFI PART"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(12, 4), 92);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(24, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(32, 8), (ulong)sectorCount - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(40, 8), 34);
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(48, 8), (ulong)sectorCount - entriesSectors - 2);
        Guid.NewGuid().TryWriteBytes(header.Slice(56, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(header.Slice(72, 8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(80, 4), entryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(84, 4), entrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(88, 4), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), HybridGptDiskWriter.ComputeCrc32(header[..92]));

        entries.CopyTo(bytes.AsSpan((sectorCount - entriesSectors - 1) * SectorSize, entries.Length));
        header.CopyTo(bytes.AsSpan((sectorCount - 1) * SectorSize, SectorSize));
        var backup = bytes.AsSpan((sectorCount - 1) * SectorSize, SectorSize);
        BinaryPrimitives.WriteUInt64LittleEndian(backup.Slice(24, 8), (ulong)sectorCount - 1);
        BinaryPrimitives.WriteUInt64LittleEndian(backup.Slice(32, 8), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(backup.Slice(72, 8), (ulong)sectorCount - entriesSectors - 1);
        BinaryPrimitives.WriteUInt32LittleEndian(backup.Slice(16, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(backup.Slice(16, 4), HybridGptDiskWriter.ComputeCrc32(backup[..92]));
        return new MemoryStream(bytes, writable: false);
    }

    private static bool HeaderCrcIsValid(ReadOnlySpan<byte> header)
    {
        var expected = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
        var copy = header[..92].ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(16, 4), 0);
        return HybridGptDiskWriter.ComputeCrc32(copy) == expected;
    }
}

internal static class HybridGptDiskWriterTestsAccess
{
    internal static byte[] Image(int sectors) =>
        ((MemoryStream)typeof(HybridGptDiskWriterTests).GetMethod("BuildHybridImage", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [sectors])!).ToArray();
}
