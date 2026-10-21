using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace DexTor.Provisioner;

/// <summary>Takes the volumes of a disk away from Windows for the length of a raw write.</summary>
public interface IVolumeLocker
{
    /// <summary>
    /// Removes the letters of every volume on <paramref name="diskNumber"/>, locks and
    /// dismounts them, and holds them until disposed; null when any of them refused, in
    /// which case nothing is held.
    /// </summary>
    IDisposable? TryLockVolumesOn(int diskNumber);
}

/// <summary>
/// Locks a disk's volumes so the new partition table can be written over the old one.
/// </summary>
/// <remarks>
/// The stick used to be prepared with diskpart's <c>clean</c>. That leaves it, for a
/// moment, with no partition table at all, and Windows shows a removable disk in that
/// state as one raw volume over the whole disk, under its old letter - which is what
/// produced "You need to format the disk in drive F:" and "F:\ is not accessible", and
/// no AutoPlay setting reaches those prompts. Imaging tools avoid the state instead:
/// every volume on the disk loses its letter, is locked and dismounted, and stays locked
/// while the image - partition table included - is written over the old layout in one
/// pass. Windows never sees an empty disk, so there is nothing for it to offer to format.
/// </remarks>
public sealed partial class WindowsVolumeLocker : IVolumeLocker
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareReadWrite = 0x3;
    private const uint OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;

    public IDisposable? TryLockVolumesOn(int diskNumber)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var held = new List<SafeFileHandle>();
        try
        {
            foreach (var volume in EnumerateVolumes())
            {
                using var probe = Open(volume);
                if (probe.IsInvalid || !LiesOn(probe, diskNumber)) continue;
                probe.Close();

                // The letter goes first, so the volume cannot come back under it.
                foreach (var mountPoint in MountPointsOf(volume))
                {
                    _ = DeleteVolumeMountPointW(mountPoint);
                }

                var handle = Open(volume);
                if (handle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Volume {volume} could not be opened.");
                }

                held.Add(handle);
                if (!LockWithRetries(handle))
                {
                    // A lock refused because something has a file open is overridden by
                    // the dismount below; only a failed dismount stops the attempt.
                }

                if (!DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), $"Volume {volume} could not be dismounted.");
                }
            }

            return new HeldVolumes(held);
        }
        catch (Win32Exception)
        {
            foreach (var handle in held) handle.Dispose();
            return null;
        }
    }

    private static bool LockWithRetries(SafeFileHandle handle)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (DeviceIoControl(handle, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return false;
    }

    private static SafeFileHandle Open(string volumeName) =>
        CreateFileW(volumeName.TrimEnd('\\'), GenericRead | GenericWrite, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    /// <summary>Whether any extent of the volume is on the disk.</summary>
    private static unsafe bool LiesOn(SafeFileHandle volume, int diskNumber)
    {
        // VOLUME_DISK_EXTENTS: a count, then { DiskNumber, StartingOffset, ExtentLength } entries.
        const int bufferSize = 8 + 24 * 32;
        var buffer = stackalloc byte[bufferSize];
        if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, (IntPtr)buffer, bufferSize, out _, IntPtr.Zero))
        {
            return false;
        }

        var count = *(uint*)buffer;
        for (var index = 0; index < count && index < 32; index++)
        {
            if (*(uint*)(buffer + 8 + index * 24) == (uint)diskNumber) return true;
        }

        return false;
    }

    private static unsafe List<string> EnumerateVolumes()
    {
        const int length = 64;
        var volumes = new List<string>();
        var name = stackalloc char[length];
        using var find = FindFirstVolumeW(name, length);
        if (find.IsInvalid) return volumes;
        do
        {
            volumes.Add(new string(name));
        }
        while (FindNextVolumeW(find, name, length));
        return volumes;
    }

    private static unsafe string[] MountPointsOf(string volumeName)
    {
        const int length = 1024;
        var buffer = stackalloc char[length];
        if (!GetVolumePathNamesForVolumeNameW(volumeName, buffer, length, out var written) || written == 0)
        {
            return [];
        }

        return new string(buffer, 0, (int)Math.Min(written, length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class HeldVolumes(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (var handle in handles) handle.Dispose();
            handles.Clear();
        }
    }

    internal sealed class VolumeFindHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => FindVolumeClose(handle);
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint controlCode, IntPtr inBuffer, int inBufferSize, IntPtr outBuffer, int outBufferSize, out uint bytesReturned, IntPtr overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial VolumeFindHandle FindFirstVolumeW(char* volumeName, int length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool FindNextVolumeW(VolumeFindHandle find, char* volumeName, int length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindVolumeClose(IntPtr find);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathNamesForVolumeNameW(string volumeName, char* names, int length, out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteVolumeMountPointW(string mountPoint);
}
