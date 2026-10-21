using System.Runtime.InteropServices;
using DexTor.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DexTor.Provisioner;

/// <summary>
/// Gives the private <c>data.img</c> its full size without writing it, reading as zeros throughout.
/// </summary>
/// <remarks>
/// <para>
/// A plain file sized with SetLength makes NTFS zero-fill everything below each new write
/// offset first, and the guest stalled for minutes (see <see cref="DataImageValidData"/>).
/// That used to be avoided by writing the whole file once: on a 64 GB stick about 25 GB of
/// zeros, 25 minutes at a stick's 20 MB/s - for a 1.7 GB image.
/// </para>
/// <para>
/// The file is made sparse instead. Nothing is allocated until the guest writes, a write
/// anywhere allocates only that 64 KiB unit (measured: 36 ms for 4 KiB at 15 GB into an empty
/// 20 GB file), and every range never written reads as zeros. That matters: DexTor OS's
/// SecureStorage calls a disk new only when its edges and 64 probes across it read zero, so
/// a file whose valid data length was merely moved (SetFileValidData, tried in 0.1.34) showed
/// the stick's old contents and the storage could not be created - and handed those old
/// contents to the guest.
/// </para>
/// <para>
/// Where the volume cannot make the file sparse, the old full pass is used, so a stick is
/// never left with an image the VM would stall on.
/// </para>
/// </remarks>
internal static partial class DataImageAllocator
{
    /// <summary>
    /// Creates <paramref name="path"/> at <paramref name="length"/> bytes, all of it reading as
    /// zeros. Returns true when that took no pass over the file.
    /// </summary>
    internal static bool Create(
        string path,
        long length,
        Action<long, long>? progress,
        CancellationToken cancellationToken,
        Func<SafeFileHandle, bool>? makeSparse = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        makeSparse ??= TryMakeSparse;
        bool sparse;
        using (var image = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough))
        {
            // Sparse before it has a size: the length then allocates nothing.
            sparse = makeSparse(image.SafeFileHandle);
            image.SetLength(length);
            DurableFlush.Flush(image);
        }

        // Nothing was written, so no bytes are reported: a figure of gigabytes "written" in an
        // instant would read as the stick's write speed and ruin the time-left estimate.
        if (!sparse)
        {
            DataImageValidData.Extend(path, progress, cancellationToken);
        }

        return sparse;
    }

    /// <summary>How much of <paramref name="path"/> is actually on the disk.</summary>
    internal static long AllocatedBytes(string path)
    {
        var low = GetCompressedFileSizeW(path, out var high);
        if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0) throw new IOException($"Cannot read the allocated size of {path}.");
        return ((long)high << 32) | low;
    }

    private static bool TryMakeSparse(SafeFileHandle file) =>
        DeviceIoControl(file, FsctlSetSparse, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);

    private const uint FsctlSetSparse = 0x000900C4;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
