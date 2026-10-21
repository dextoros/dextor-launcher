using System.Buffers.Binary;

namespace DexTor.Provisioner;

public interface IHybridDiskWriter
{
    /// <param name="progress">Bytes written, and then read back when <paramref name="verify"/> is set.</param>
    /// <param name="verify">Read the image back at once; false leaves it to <see cref="VerifyAsync"/>.</param>
    Task<HybridGptLayout> WriteAsync(
        string imagePath,
        int diskNumber,
        long diskCapacityBytes,
        IProgress<long>? progress = null,
        bool verify = true,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the written image back from the disk and compares it with the file; throws when they differ.</summary>
    /// <param name="progress">Bytes compared, out of the image size.</param>
    Task VerifyAsync(
        string imagePath,
        int diskNumber,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <summary>The disk's logical sector size in bytes, or null when it cannot be read.</summary>
    int? GetLogicalSectorSize(int diskNumber) => null;

    /// <summary>
    /// Replaces the disk's layout with an empty GPT in one request; false when that was
    /// not possible and the old layout is still there.
    /// </summary>
    bool TryResetPartitionTable(int diskNumber) => false;
}

public sealed partial class PhysicalHybridDiskWriter : IHybridDiskWriter
{
    public async Task<HybridGptLayout> WriteAsync(
        string imagePath,
        int diskNumber,
        long diskCapacityBytes,
        IProgress<long>? progress = null,
        bool verify = true,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Raw USB provisioning is supported only on Windows.");
        }

        var physicalDrive = $@"\\.\PhysicalDrive{diskNumber}";
        await using var source = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = await OpenPhysicalDriveAsync(
            () => new FileStream(
                physicalDrive,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.ReadWrite,
                4 * 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var layout = await HybridGptDiskWriter.WriteAsync(
            source,
            destination,
            diskCapacityBytes,
            progress,
            verify,
            cancellationToken).ConfigureAwait(false);
        DurableFlush.Flush(destination);
        RefreshPartitionTable(destination.SafeFileHandle);
        return layout;
    }

    /// <summary>
    /// Reads the image back from the physical disk. A disk handle is never served from the
    /// Windows file cache, so what is compared is what the stick holds.
    /// </summary>
    public async Task VerifyAsync(
        string imagePath,
        int diskNumber,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Raw USB provisioning is supported only on Windows.");
        }

        await using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4 * 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        // Unbuffered: what is compared comes from the stick, never from memory.
        await using var disk = await OpenPhysicalDriveAsync(
            () => UnbufferedReadStream.OpenDevice($@"\\.\PhysicalDrive{diskNumber}"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await HybridGptDiskWriter.VerifyAsync(source, disk, progress, cancellationToken).ConfigureAwait(false);
    }

    private const uint IoctlDiskUpdateProperties = 0x00070140;
    private const uint IoctlDiskGetDriveGeometryEx = 0x000700A0;
    private const uint IoctlDiskCreateDisk = 0x0007C058;

    /// <summary>
    /// Gives the disk an empty GPT in one request, as imaging tools do before they write.
    /// </summary>
    /// <remarks>
    /// Writing over whatever layout the stick carried - including one left half-done by an
    /// earlier failed attempt - failed on one machine until another tool had reinitialized
    /// the disk. This is that reinitialization. Unlike diskpart's <c>clean</c>, there is no
    /// moment in which the disk has no table at all, so Windows does not show it as a raw
    /// volume to be formatted. It runs while the disk's volumes are locked.
    /// </remarks>
    public unsafe bool TryResetPartitionTable(int diskNumber)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var disk = File.OpenHandle($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            // CREATE_DISK { PARTITION_STYLE PartitionStyle; CREATE_DISK_GPT { GUID DiskId; DWORD MaxPartitionCount; } }
            var request = stackalloc byte[24];
            new Span<byte>(request, 24).Clear();
            *(int*)request = 1; // PARTITION_STYLE_GPT
            Guid.NewGuid().TryWriteBytes(new Span<byte>(request + 4, 16));
            *(int*)(request + 20) = 128;
            if (!DeviceIoControl(disk, IoctlDiskCreateDisk, (IntPtr)request, 24, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                return false;
            }

            _ = DeviceIoControl(disk, IoctlDiskUpdateProperties, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public unsafe int? GetLogicalSectorSize(int diskNumber)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var disk = File.OpenHandle($@"\\.\PhysicalDrive{diskNumber}", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            // DISK_GEOMETRY_EX starts with DISK_GEOMETRY, whose BytesPerSector is at offset 20.
            const int size = 256;
            var buffer = stackalloc byte[size];
            if (!DeviceIoControl(disk, IoctlDiskGetDriveGeometryEx, IntPtr.Zero, 0, (IntPtr)buffer, size, out _, IntPtr.Zero))
            {
                return null;
            }

            return *(int*)(buffer + 20);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells Windows the partition table on the disk has changed.
    /// </summary>
    /// <remarks>
    /// The table was written as raw sectors, which Windows does not notice by itself. Until
    /// it re-reads the table it knows no data partition, and formatting it fails. This is
    /// the same request imaging tools make after a raw write; a refusal is not fatal
    /// because the format step still rescans and retries.
    /// </remarks>
    internal static void RefreshPartitionTable(Microsoft.Win32.SafeHandles.SafeFileHandle disk)
    {
        if (!OperatingSystem.IsWindows() || disk.IsInvalid || disk.IsClosed) return;
        _ = DeviceIoControl(disk, IoctlDiskUpdateProperties, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    internal static async Task<TStream> OpenPhysicalDriveAsync<TStream>(
        Func<TStream> open,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxAttempts = 60,
        CancellationToken cancellationToken = default)
        where TStream : Stream
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        delay ??= Task.Delay;
        IOException? lastError = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return open();
            }
            catch (IOException exception)
            {
                lastError = exception;
                if (attempt + 1 < maxAttempts)
                {
                    await delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new IOException(
            "Windows did not make the selected USB ready for writing. Reconnect it and retry creation.",
            lastError);
    }
}

public sealed record HybridGptLayout(
    ulong DataPartitionFirstLba,
    ulong DataPartitionLastLba,
    ulong BackupHeaderLba,
    int DataPartitionEntryIndex);

public static class HybridGptDiskWriter
{
    public const int SectorSize = 512;
    public const ulong AlignmentSectors = 2048;
    public const ulong NoDefaultDriveLetterAttribute = 0x8000000000000000;

    /// <summary>GPT_BASIC_DATA_ATTRIBUTE_HIDDEN: the mount manager does not mount the volume.</summary>
    public const ulong HiddenAttribute = 0x4000000000000000;

    /// <summary>What the image's own basic-data partitions are marked with.</summary>
    public const ulong ImagePartitionHidingAttributes = NoDefaultDriveLetterAttribute | HiddenAttribute;
    public static readonly Guid MicrosoftBasicDataPartitionType =
        new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    /// <summary>
    /// Compares the disk with the image it was written from, leaving out what the write
    /// changed on purpose: the hidden-partition flags, the primary GPT and the old backup
    /// table that the grown GPT replaced.
    /// </summary>
    /// <param name="progress">Bytes compared, out of the image size.</param>
    public static Task VerifyAsync(
        Stream hybridIso,
        Stream disk,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hybridIso);
        ArgumentNullException.ThrowIfNull(disk);
        var header = ReadAt(hybridIso, SectorSize, SectorSize);
        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72, 8));
        var entriesLength = checked((long)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4))
            * BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4)));
        var entriesSectors = checked((ulong)((entriesLength + SectorSize - 1) / SectorSize));
        var backupEntriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32, 8)) - entriesSectors;
        return VerifyCopiedImageAsync(
            hybridIso,
            disk,
            hybridIso.Length,
            checked((long)entriesLba * SectorSize),
            entriesLength,
            checked((long)backupEntriesLba * SectorSize),
            progress is null ? null : new InlineProgress<long>(progress.Report),
            cancellationToken);
    }

    /// <param name="verify">Read the image back at once; false leaves it to <see cref="VerifyAsync(Stream, Stream, IProgress{long}?, CancellationToken)"/>.</param>
    public static async Task<HybridGptLayout> WriteAsync(
        Stream hybridIso,
        Stream destinationDisk,
        long diskCapacityBytes,
        IProgress<long>? progress = null,
        bool verify = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hybridIso);
        ArgumentNullException.ThrowIfNull(destinationDisk);
        if (!hybridIso.CanRead || !hybridIso.CanSeek)
        {
            throw new ArgumentException("The hybrid ISO stream must be readable and seekable.", nameof(hybridIso));
        }

        if (!destinationDisk.CanRead || !destinationDisk.CanWrite || !destinationDisk.CanSeek)
        {
            throw new ArgumentException("The destination disk stream must be readable, writable and seekable.", nameof(destinationDisk));
        }

        if (hybridIso.Length % SectorSize != 0 || diskCapacityBytes % SectorSize != 0)
        {
            throw new InvalidOperationException("The ISO and destination capacity must be sector aligned.");
        }

        var isoLength = hybridIso.Length;
        ValidateHybridBootRecords(hybridIso);
        var sourceHeader = ReadAt(hybridIso, SectorSize, SectorSize);
        var sourceEntriesLba = BinaryPrimitives.ReadUInt64LittleEndian(sourceHeader.AsSpan(72, 8));
        var sourceEntryCount = BinaryPrimitives.ReadUInt32LittleEndian(sourceHeader.AsSpan(80, 4));
        var sourceEntrySize = BinaryPrimitives.ReadUInt32LittleEndian(sourceHeader.AsSpan(84, 4));
        var sourceEntriesLength = checked((long)sourceEntryCount * sourceEntrySize);
        var sourceEntriesSectors = checked((ulong)((sourceEntriesLength + SectorSize - 1) / SectorSize));
        var sourceBackupHeaderLba = BinaryPrimitives.ReadUInt64LittleEndian(sourceHeader.AsSpan(32, 8));
        var sourceBackupEntriesLba = sourceBackupHeaderLba - sourceEntriesSectors;
        if (diskCapacityBytes <= isoLength + (long)(AlignmentSectors * SectorSize))
        {
            throw new InvalidOperationException("The USB has no room for the DexTor data partition.");
        }

        // The partition table is written last. Written first, it made Windows find the
        // image's partitions - the FAT boot partition among them - while the copy was
        // still filling them, and mount them; a raw write into a mounted volume is then
        // refused. So the body goes first, then the backup table at the end of the disk,
        // and the head of the disk - protective MBR, primary header and entries, already
        // patched hidden - closes the write, when nothing inside the new partitions is
        // left to write.
        var prefix = BuildHiddenPartitionPrefix(hybridIso, sourceHeader, sourceEntriesLba, sourceEntriesLength);
        hybridIso.Position = prefix.Length;
        destinationDisk.Position = prefix.Length;
        var copyBuffer = new byte[4 * 1024 * 1024];
        long written = prefix.Length;
        progress?.Report(written);
        int read;
        while ((read = await hybridIso.ReadAsync(copyBuffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            try
            {
                await destinationDisk.WriteAsync(copyBuffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                // Where the device stopped is the most useful thing to know about it: the same
                // place twice over points at a faulty stick or a fake capacity.
                throw new DiskWriteException(written, exception);
            }

            written += read;
            progress?.Report(written);
        }
        await destinationDisk.FlushAsync(cancellationToken).ConfigureAwait(false);

        using var head = new MemoryStream(prefix, writable: true);
        var layout = ExpandGpt(head, destinationDisk, isoLength, diskCapacityBytes);
        destinationDisk.Position = 0;
        await destinationDisk.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await destinationDisk.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!verify) return layout;
        await VerifyCopiedImageAsync(
            hybridIso,
            destinationDisk,
            isoLength,
            checked((long)sourceEntriesLba * SectorSize),
            sourceEntriesLength,
            checked((long)sourceBackupEntriesLba * SectorSize),
            progress is null ? null : new InlineProgress<long>(checkedBytes => progress.Report(isoLength + checkedBytes)),
            cancellationToken).ConfigureAwait(false);
        return layout;
    }

    /// <summary>
    /// The start of the image up to the end of its primary partition table, with the
    /// image's basic-data partitions marked hidden and letterless and both CRCs redone.
    /// </summary>
    /// <remarks>
    /// Written as the image's partitions stood, Windows saw the ISO9660 and gap partitions
    /// as ordinary data volumes, gave them letters and asked "You need to format the disk
    /// in drive G:" for each one, during and after creation. Hidden plus no default drive
    /// letter keeps the mount manager off them. Both bits are Microsoft-defined for basic
    /// data partitions only; firmware, GRUB and Linux ignore them, so direct boot is not
    /// affected.
    /// </remarks>
    internal static byte[] BuildHiddenPartitionPrefix(Stream image, byte[] header, ulong entriesLba, long entriesLength)
    {
        var entriesOffset = checked((long)entriesLba * SectorSize);
        var prefixLength = checked((int)(entriesOffset + (entriesLength + SectorSize - 1) / SectorSize * SectorSize));
        var prefix = ReadAt(image, 0, prefixLength);
        var entryCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4)));
        var entrySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4)));
        var entries = prefix.AsSpan(checked((int)entriesOffset), checked((int)entriesLength));
        HideImageDataPartitions(entries, entryCount, entrySize);
        var patchedHeader = prefix.AsSpan(SectorSize, SectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(patchedHeader[88..92], ComputeCrc32(entries));
        BinaryPrimitives.WriteUInt32LittleEndian(patchedHeader[16..20], 0);
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(patchedHeader[12..16]));
        BinaryPrimitives.WriteUInt32LittleEndian(patchedHeader[16..20], ComputeCrc32(patchedHeader[..headerSize]));
        return prefix;
    }

    private static void HideImageDataPartitions(Span<byte> entries, int entryCount, int entrySize)
    {
        for (var index = 0; index < entryCount; index++)
        {
            var entry = entries.Slice(index * entrySize, entrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0
                || new Guid(entry[..16]) != MicrosoftBasicDataPartitionType)
            {
                continue;
            }

            var attributes = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(48, 8));
            BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(48, 8), attributes | ImagePartitionHidingAttributes);
        }
    }

    internal static void ValidateHybridBootRecords(Stream image)
    {
        var sector = ReadAt(image, 0, SectorSize);
        if (sector[510] != 0x55 || sector[511] != 0xAA)
        {
            throw new InvalidOperationException("The Live image does not contain a bootable MBR.");
        }

        if (sector.AsSpan(0, 440).IndexOfAnyExcept((byte)0) < 0)
        {
            throw new InvalidOperationException("The Live image MBR contains no BIOS boot code.");
        }

        var protectiveEntry = sector.AsSpan(446, 16);
        if (protectiveEntry[4] != 0xEE || BinaryPrimitives.ReadUInt32LittleEndian(protectiveEntry[8..12]) != 1)
        {
            throw new InvalidOperationException("The Live image does not contain a protective GPT MBR.");
        }

        var header = ReadAt(image, SectorSize, SectorSize);
        if (!header.AsSpan(0, 8).SequenceEqual("EFI PART"u8))
        {
            throw new InvalidOperationException("The Live image does not contain a GPT header.");
        }

        ValidateHeaderCrc(header);
        ValidateGptStructure(image, header);
    }

    private static void ValidateGptStructure(Stream image, byte[] header)
    {
        var imageSectors = checked((ulong)(image.Length / SectorSize));
        var currentLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24, 8));
        var backupLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32, 8));
        var entriesLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(72, 8));
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(80, 4));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(84, 4));
        var lastUsableLba = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(48, 8));
        if (currentLba != 1
            || backupLba >= imageSectors
            || entryCount == 0
            || entrySize < 128
            || entrySize > 4096)
        {
            throw new InvalidOperationException("The Live image GPT geometry is invalid.");
        }

        var entriesByteLength64 = checked((ulong)entryCount * entrySize);
        var entriesSectorCount = (entriesByteLength64 + SectorSize - 1) / SectorSize;
        if (entriesByteLength64 > int.MaxValue
            || entriesLba >= imageSectors
            || backupLba <= entriesSectorCount
            || lastUsableLba >= backupLba - entriesSectorCount
            || entriesByteLength64 > checked((imageSectors - entriesLba) * SectorSize))
        {
            throw new InvalidOperationException("The Live image GPT partition table is outside the image.");
        }

        var entries = ReadAt(image, checked((long)entriesLba * SectorSize), checked((int)entriesByteLength64));
        if (ComputeCrc32(entries) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(88, 4)))
        {
            throw new InvalidOperationException("The Live image GPT partition table checksum is invalid.");
        }

        if (FindFreeEntry(entries, checked((int)entryCount), checked((int)entrySize)) < 0)
        {
            throw new InvalidOperationException("The Live image GPT has no free partition entry.");
        }

        for (var index = 0; index < entryCount; index++)
        {
            var entry = entries.AsSpan(checked((int)(index * entrySize)), checked((int)entrySize));
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0)
            {
                continue;
            }

            var firstLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(32, 8));
            var lastLba = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(40, 8));
            if (firstLba > lastLba || lastLba > lastUsableLba)
            {
                throw new InvalidOperationException("The Live image contains an invalid GPT partition range.");
            }
        }
    }

    private static async Task VerifyCopiedImageAsync(
        Stream source,
        Stream destination,
        long imageLength,
        long primaryEntriesOffset,
        long primaryEntriesLength,
        long oldBackupEntriesOffset,
        InlineProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        var excluded = new (long Start, long End)[]
        {
            (458, 462),
            (SectorSize, 2L * SectorSize),
            (primaryEntriesOffset, primaryEntriesOffset + primaryEntriesLength),
            (oldBackupEntriesOffset, imageLength)
        };
        var sourceBuffer = new byte[4 * 1024 * 1024];
        var destinationBuffer = new byte[sourceBuffer.Length];
        for (long offset = 0; offset < imageLength; offset += sourceBuffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(sourceBuffer.Length, imageLength - offset);
            source.Position = offset;
            destination.Position = offset;
            await source.ReadExactlyAsync(sourceBuffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            await destination.ReadExactlyAsync(destinationBuffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);

            foreach (var range in excluded)
            {
                var start = Math.Max(offset, range.Start);
                var end = Math.Min(offset + count, range.End);
                if (start < end)
                {
                    sourceBuffer.AsSpan((int)(start - offset), (int)(end - start))
                        .CopyTo(destinationBuffer.AsSpan((int)(start - offset), (int)(end - start)));
                }
            }

            if (!sourceBuffer.AsSpan(0, count).SequenceEqual(destinationBuffer.AsSpan(0, count)))
            {
                throw new IOException($"Hybrid image read-back verification failed near byte {offset}.");
            }

            progress?.Report(offset + count);
        }
    }

    /// <summary>
    /// Grows the GPT to the disk: <paramref name="head"/> holds the start of the disk
    /// (MBR, primary header and entries) and is updated in place for the caller to write
    /// last; the structures at the end of the disk are written to <paramref name="disk"/>.
    /// </summary>
    private static HybridGptLayout ExpandGpt(Stream head, Stream disk, long isoLength, long diskCapacityBytes)
    {
        var primaryHeader = ReadAt(head, SectorSize, SectorSize);
        var entryCount = BinaryPrimitives.ReadUInt32LittleEndian(primaryHeader.AsSpan(80, 4));
        var entrySize = BinaryPrimitives.ReadUInt32LittleEndian(primaryHeader.AsSpan(84, 4));
        var primaryEntriesLba = BinaryPrimitives.ReadUInt64LittleEndian(primaryHeader.AsSpan(72, 8));
        if (entryCount == 0 || entrySize < 128 || entrySize > 4096)
        {
            throw new InvalidOperationException("The Live image GPT partition table is invalid.");
        }

        var entriesByteLength = checked((int)(entryCount * entrySize));
        var entriesSectorCount = checked((ulong)((entriesByteLength + SectorSize - 1) / SectorSize));
        var entries = ReadAt(head, checked((long)primaryEntriesLba * SectorSize), entriesByteLength);
        var expectedEntriesCrc = BinaryPrimitives.ReadUInt32LittleEndian(primaryHeader.AsSpan(88, 4));
        if (ComputeCrc32(entries) != expectedEntriesCrc)
        {
            throw new InvalidOperationException("The Live image GPT partition table checksum is invalid.");
        }

        var freeEntryIndex = FindFreeEntry(entries, checked((int)entryCount), checked((int)entrySize));
        if (freeEntryIndex < 0)
        {
            throw new InvalidOperationException("The Live image GPT has no free partition entry.");
        }

        SuppressAutomaticMountForExistingDataPartitions(
            entries,
            checked((int)entryCount),
            checked((int)entrySize));

        var diskLastLba = checked((ulong)(diskCapacityBytes / SectorSize - 1));
        var backupEntriesLba = diskLastLba - entriesSectorCount;
        var newLastUsableLba = backupEntriesLba - 1;
        var isoSectors = checked((ulong)(isoLength / SectorSize));
        var dataFirstLba = AlignUp(isoSectors, AlignmentSectors);
        if (dataFirstLba > newLastUsableLba)
        {
            throw new InvalidOperationException("The USB has no usable space after the hybrid ISO.");
        }

        WritePartitionEntry(
            entries.AsSpan(freeEntryIndex * checked((int)entrySize), checked((int)entrySize)),
            dataFirstLba,
            newLastUsableLba);
        var entriesCrc = ComputeCrc32(entries);

        // The ISO's old backup GPT is no longer authoritative once the disk grows.
        var oldBackupHeaderLba = BinaryPrimitives.ReadUInt64LittleEndian(primaryHeader.AsSpan(32, 8));
        var oldBackupEntriesLba = oldBackupHeaderLba - entriesSectorCount;
        ZeroAt(disk, checked((long)oldBackupEntriesLba * SectorSize), checked((int)((entriesSectorCount + 1) * SectorSize)));

        WriteAt(head, checked((long)primaryEntriesLba * SectorSize), entries);
        var paddedEntries = new byte[checked((int)(entriesSectorCount * SectorSize))];
        entries.CopyTo(paddedEntries, 0);
        WriteAt(disk, checked((long)backupEntriesLba * SectorSize), paddedEntries);

        UpdateHeader(primaryHeader, 1, diskLastLba, newLastUsableLba, primaryEntriesLba, entriesCrc);
        WriteAt(head, SectorSize, primaryHeader);

        var backupHeader = (byte[])primaryHeader.Clone();
        UpdateHeader(backupHeader, diskLastLba, 1, newLastUsableLba, backupEntriesLba, entriesCrc);
        WriteAt(disk, checked((long)diskLastLba * SectorSize), backupHeader);

        var mbr = ReadAt(head, 0, SectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            mbr.AsSpan(458, 4),
            diskLastLba > uint.MaxValue ? uint.MaxValue : (uint)diskLastLba);
        WriteAt(head, 0, mbr);
        disk.Flush();

        return new HybridGptLayout(dataFirstLba, newLastUsableLba, diskLastLba, freeEntryIndex + 1);
    }

    private static void WritePartitionEntry(Span<byte> entry, ulong firstLba, ulong lastLba)
    {
        entry.Clear();
        MicrosoftBasicDataPartitionType.TryWriteBytes(entry[..16]);
        Guid.NewGuid().TryWriteBytes(entry.Slice(16, 16));
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(32, 8), firstLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(40, 8), lastLba);
        BinaryPrimitives.WriteUInt64LittleEndian(entry.Slice(48, 8), NoDefaultDriveLetterAttribute);
        "DEXTOR".AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(entry.Slice(56, 72)));
    }

    private static void SuppressAutomaticMountForExistingDataPartitions(byte[] entries, int entryCount, int entrySize)
    {
        for (var index = 0; index < entryCount; index++)
        {
            var entry = entries.AsSpan(index * entrySize, entrySize);
            if (entry[..16].IndexOfAnyExcept((byte)0) < 0
                || new Guid(entry[..16]) != MicrosoftBasicDataPartitionType)
            {
                continue;
            }

            var attributes = BinaryPrimitives.ReadUInt64LittleEndian(entry.Slice(48, 8));
            BinaryPrimitives.WriteUInt64LittleEndian(
                entry.Slice(48, 8),
                attributes | ImagePartitionHidingAttributes);
        }
    }

    private static int FindFreeEntry(byte[] entries, int entryCount, int entrySize)
    {
        for (var index = 0; index < entryCount; index++)
        {
            if (entries.AsSpan(index * entrySize, 16).IndexOfAnyExcept((byte)0) < 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static void UpdateHeader(
        byte[] header,
        ulong currentLba,
        ulong alternateLba,
        ulong lastUsableLba,
        ulong entriesLba,
        uint entriesCrc)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(24, 8), currentLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32, 8), alternateLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(48, 8), lastUsableLba);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(72, 8), entriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(88, 4), entriesCrc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 0);
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4)));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), ComputeCrc32(header.AsSpan(0, headerSize)));
    }

    private static void ValidateHeaderCrc(byte[] header)
    {
        var headerSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4)));
        if (headerSize < 92 || headerSize > SectorSize)
        {
            throw new InvalidOperationException("The Live image GPT header size is invalid.");
        }

        var expected = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));
        var copy = header.AsSpan(0, headerSize).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(16, 4), 0);
        if (ComputeCrc32(copy) != expected)
        {
            throw new InvalidOperationException("The Live image GPT header checksum is invalid.");
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        checked(((value + alignment - 1) / alignment) * alignment);

    private static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }

    private static void WriteAt(Stream stream, long offset, byte[] buffer)
    {
        stream.Position = offset;
        stream.Write(buffer);
    }

    private static void ZeroAt(Stream stream, long offset, int count)
    {
        WriteAt(stream, offset, new byte[count]);
    }

    internal static uint ComputeCrc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }
}

/// <summary>Reports on the calling thread, in order - unlike <see cref="Progress{T}"/>.</summary>
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>A raw write to the disk that the device refused, and where.</summary>
public sealed class DiskWriteException(long offset, IOException inner)
    : IOException($"The USB drive refused to store data at byte {offset}: {inner.Message}", inner)
{
    public long Offset { get; } = offset;

    public int ErrorCode { get; } = inner.HResult & 0xFFFF;
}
