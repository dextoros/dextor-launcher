using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DexTor.Qemu;

/// <summary>
/// Resolves a host path to a canonical identity for the file it actually names.
/// </summary>
/// <remarks>
/// Session ownership must satisfy one invariant: <em>the same underlying supported
/// storage yields the same ownership identity</em>. A path string cannot carry that
/// on its own. The same DexTor USB is <c>E:\</c> in one logon session and <c>F:\</c>
/// in another; the same image is reachable through <c>\\?\</c> prefixes, 8.3 short
/// names, directory junctions and mounted folders. Comparing uppercased path strings
/// treats every one of those as a different device and would license a second QEMU
/// writer on one <c>data.img</c>.
/// </remarks>
public interface IPersistentStorageCanonicalizer
{
    /// <summary>
    /// Returns a stable identity for the file at <paramref name="dataImagePath"/>.
    /// </summary>
    /// <exception cref="VmPrelaunchException">
    /// The path does not name a file whose identity can be established. Callers must
    /// treat this as fail-closed: no canonical identity means no provable exclusion.
    /// </exception>
    string Canonicalize(string dataImagePath);
}

/// <summary>
/// Canonicalizes through the Windows object manager: the volume GUID path of the
/// file the handle finally resolves to.
/// </summary>
/// <remarks>
/// <c>GetFinalPathNameByHandle</c> with <c>VOLUME_NAME_GUID</c> resolves drive-letter
/// assignment, <c>subst</c> mappings, 8.3 short names, directory junctions, symbolic
/// links and mounted folders down to one <c>\\?\Volume{GUID}\…</c> spelling, because
/// it reports what the kernel actually opened rather than what the caller typed. Two
/// aliases of one <c>data.img</c> therefore produce one identity, and one identity
/// produces one mutex name.
/// <para>
/// Anything that cannot be resolved that way — a network share, a volume with no GUID,
/// a file that cannot be opened — is rejected rather than downgraded to its path
/// string. An unidentifiable image is exactly the case where a second writer must not
/// be allowed to assume it is looking at a different device.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsVolumeStorageCanonicalizer : IPersistentStorageCanonicalizer
{
    /// <summary>Resolve junctions and short names to the normalized final name.</summary>
    private const uint FileNameNormalized = 0x0;

    /// <summary>
    /// Report the volume as <c>\\?\Volume{GUID}\</c> rather than as a drive letter. The
    /// GUID is a property of the volume itself, so it survives a different drive-letter
    /// assignment in another logon session, unlike <c>VOLUME_NAME_NT</c> (0x2), which
    /// names a device number that can change across remounts.
    /// </summary>
    private const uint VolumeNameGuid = 0x1;

    private const string VolumeGuidPrefix = @"\\?\VOLUME{";

    public static WindowsVolumeStorageCanonicalizer Default { get; } = new();

    public string Canonicalize(string dataImagePath)
    {
        if (string.IsNullOrWhiteSpace(dataImagePath))
        {
            throw new VmPrelaunchException("A persistent storage image path is required.");
        }

        using var handle = OpenForIdentity(dataImagePath);
        RejectMultiplyLinkedImage(handle);
        var finalName = ReadFinalPath(handle);

        // Uppercase for the same reason Windows paths compare case-insensitively: two
        // spellings that differ only in case name one file, not two.
        var canonical = finalName.ToUpperInvariant();
        if (!canonical.StartsWith(VolumeGuidPrefix, StringComparison.Ordinal))
        {
            throw new VmPrelaunchException(
                "The persistent USB data image is not on a local volume DexTor can identify "
                + "uniquely, so exclusive use of it cannot be guaranteed. Run DexTor from the "
                + "USB drive itself rather than through a network location.");
        }

        return canonical;
    }

    /// <summary>
    /// Refuses an image that has more than one directory entry pointing at it.
    /// </summary>
    /// <remarks>
    /// The supported layout is an NTFS volume (the stick's DEXTOR partition), and NTFS
    /// supports hard links. A final path is the name a handle was opened <em>by</em>, so
    /// two hard links to one <c>data.img</c> normalize to two different volume-GUID paths
    /// and would key two different mutexes for a single file — two QEMU writers on the
    /// same bytes. Nothing about the normalization can tell those apart, because as far
    /// as the object manager is concerned they are two valid names.
    /// <para>
    /// Rather than switching identity to a file id — which would be a second scheme that
    /// only some filesystems answer, so one image could get different mutex names
    /// depending on which call succeeded — a multiply linked image is rejected before
    /// startup. Provisioning never creates one, so this refuses a state DexTor did not
    /// produce instead of guessing at it. FAT and exFAT report one link, so the check is
    /// inert there.
    /// </para>
    /// </remarks>
    private static void RejectMultiplyLinkedImage(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw new VmPrelaunchException(
                "The persistent USB data image cannot be identified "
                + $"(Windows error {Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture)}), "
                + "so exclusive use of it cannot be guaranteed.");
        }

        if (information.NumberOfLinks > 1)
        {
            throw new VmPrelaunchException(
                "The persistent USB data image has more than one name on this volume "
                + $"({information.NumberOfLinks.ToString(CultureInfo.InvariantCulture)} hard links). "
                + "DexTor cannot guarantee that two launchers reaching it by different names "
                + "would recognise it as one device, so it will not start from it. Provisioning "
                + "never creates extra links; remove them, or re-create the DexTor USB.");
        }
    }

    private static SafeFileHandle OpenForIdentity(string dataImagePath)
    {
        try
        {
            // Read access with the widest sharing: this probe only identifies the file
            // and must never be the reason QEMU, or the exclusive pre-launch probe,
            // cannot open it.
            return File.OpenHandle(
                dataImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new VmPrelaunchException("The persistent USB data image is missing.");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or ArgumentException
                                          or NotSupportedException)
        {
            throw new VmPrelaunchException(
                $"The persistent USB data image cannot be identified: {exception.Message}");
        }
    }

    private static string ReadFinalPath(SafeFileHandle handle)
    {
        const uint flags = FileNameNormalized | VolumeNameGuid;

        // A volume GUID path is longer than the drive-letter spelling and MAX_PATH is not
        // a bound for it, so an undersized buffer is answered with the required length
        // (including the terminator) rather than with a truncated name.
        var buffer = new char[512];
        var written = Fill(handle, buffer, flags);
        if (written == 0)
        {
            throw FinalPathFailure();
        }

        if (written >= buffer.Length)
        {
            buffer = new char[written];
            written = Fill(handle, buffer, flags);
            if (written == 0 || written >= buffer.Length)
            {
                throw FinalPathFailure();
            }
        }

        return new string(buffer, 0, (int)written);
    }

    /// <summary>
    /// Reinterprets the UTF-16 buffer as the <c>WCHAR*</c> the API expects. <c>char</c>
    /// itself cannot cross the boundary without disabling runtime marshalling for the
    /// whole assembly, and the two have identical layout.
    /// </summary>
    private static uint Fill(SafeFileHandle handle, char[] buffer, uint flags) =>
        GetFinalPathNameByHandleW(
            handle,
            ref Unsafe.As<char, ushort>(ref buffer[0]),
            (uint)buffer.Length,
            flags);

    private static VmPrelaunchException FinalPathFailure()
    {
        var error = Marshal.GetLastPInvokeError();
        return new VmPrelaunchException(
            "The persistent USB data image cannot be identified on this volume "
            + $"(Windows error {error.ToString(System.Globalization.CultureInfo.InvariantCulture)}), "
            + "so exclusive use of it cannot be guaranteed.");
    }

    /// <summary>
    /// <c>BY_HANDLE_FILE_INFORMATION</c>. Every field is a <c>DWORD</c>, including the
    /// three <c>FILETIME</c>s, which are pairs of them. Declaring those as <c>long</c>
    /// would give the managed struct 8-byte alignment that the native one does not have,
    /// shifting every field after the first and making the link count meaningless.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformationData
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformationData information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(
        SafeFileHandle handle,
        ref ushort buffer,
        uint bufferLength,
        uint flags);
}
