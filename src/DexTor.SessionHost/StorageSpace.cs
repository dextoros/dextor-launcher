using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DexTor.SessionHost;

/// <summary>
/// Whether the stick still has room for the rest of a sparse <c>data.img</c>.
/// </summary>
/// <remarks>
/// data.img is a sparse file: it takes room only as DexTor OS writes to its storage. The
/// other half of the stick is free for the operator's own files, and if those fill it, the
/// storage cannot grow - QEMU then pauses the VM until there is room again. The launcher
/// says so before a start rather than after.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class StorageSpace
{
    /// <summary>
    /// How many bytes the stick lacks for data.img to reach its full size; 0 when it has room
    /// or when the figures cannot be read.
    /// </summary>
    public static long Shortfall(string usbRootPath)
    {
        try
        {
            var image = Path.Combine(usbRootPath, "DEXTOR", "storage", "data.img");
            if (!File.Exists(image)) return 0;
            var length = new FileInfo(image).Length;
            var low = GetCompressedFileSizeW(image, out var high);
            if (low == uint.MaxValue && Marshal.GetLastPInvokeError() != 0) return 0;
            var allocated = ((long)high << 32) | low;
            var root = Path.GetPathRoot(Path.GetFullPath(image));
            if (string.IsNullOrEmpty(root)) return 0;
            return Shortfall(length, allocated, new DriveInfo(root).AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return 0;
        }
    }

    /// <summary>What an image of <paramref name="length"/> bytes, <paramref name="allocated"/> of them on disk, still lacks.</summary>
    public static long Shortfall(long length, long allocated, long free) =>
        Math.Max(0, Math.Max(0, length - allocated) - Math.Max(0, free));

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetCompressedFileSizeW(string fileName, out uint fileSizeHigh);
}
