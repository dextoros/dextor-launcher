using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using static DexTor.SessionHost.NativeBuffer;

namespace DexTor.SessionHost;

/// <summary>
/// The Windows implementation of the device topology the resolver reasons about.
/// </summary>
/// <remarks>
/// Every method here answers with <see cref="NativeAnswer{T}"/>, so "the query failed"
/// never reaches the algorithm disguised as "there is nothing there". Variable-length
/// responses are sized by asking first, because a fixed-size guess that comes back
/// <c>ERROR_MORE_DATA</c> would otherwise be read as an ordinary failure and fall through
/// to a weaker answer.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDeviceTopology : INativeDeviceTopology
{
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlStorageGetDeviceNumber = 0x002D1080;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;

    private const int CrSuccess = 0;
    private const int CrNoSuchDevnode = 0x0000000D;

    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;

    private static readonly Guid DiskInterface = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");

    public NativeAnswer<string> VolumeGuidPathFor(string path)
    {
        var volume = WindowsVolumeFlusher.TryResolveVolumeRoot(path);
        return volume is null
            ? NativeAnswer.Failed<string>("The path is not on a resolvable local volume.")
            : NativeAnswer.Ok<string>(volume);
    }

    public VolumeStorageFacts QueryVolume(string volumeGuidPath)
    {
        using var handle = OpenDevice(volumeGuidPath.TrimEnd('\\'));
        if (handle.IsInvalid)
        {
            var error = Describe("The USB volume could not be opened", Marshal.GetLastPInvokeError());
            return new VolumeStorageFacts(
                NativeAnswer.Failed<uint[]>(error),
                NativeAnswer.Failed<bool>(error),
                NativeAnswer.Failed<int>(error));
        }

        return new VolumeStorageFacts(ReadDiskNumbers(handle), ReadRemovableMedia(handle), ReadBusType(handle));
    }

    /// <summary>
    /// Every disk the volume sits on.
    /// </summary>
    /// <remarks>
    /// <c>VOLUME_DISK_EXTENTS</c> is variable length and <c>DISK_EXTENT</c> is 8-byte
    /// aligned, so the array starts at offset 8 rather than 4. A fixed single-extent
    /// buffer is both the wrong size and the wrong shape: the call returns
    /// <c>ERROR_MORE_DATA</c> even for an ordinary one-extent volume, and reading the
    /// first disk number at offset 4 would read padding. The count is asked for first and
    /// the array is then read at its real offsets.
    /// </remarks>
    private static NativeAnswer<uint[]> ReadDiskNumbers(SafeFileHandle volume)
    {
        const int headerSize = 8;
        const int extentSize = 24;

        var probeSize = headerSize + extentSize;
        var buffer = Marshal.AllocHGlobal(probeSize);
        try
        {
            if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, (uint)probeSize, out _, IntPtr.Zero))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != ErrorMoreData && error != ErrorInsufficientBuffer)
                {
                    return ReadSingleDiskNumberFallback(volume, error);
                }

                var count = Marshal.ReadInt32(buffer);
                if (count <= 0)
                {
                    return NativeAnswer.Failed<uint[]>("The USB volume reported no disk extents.");
                }

                Marshal.FreeHGlobal(buffer);
                probeSize = headerSize + (extentSize * count);
                buffer = Marshal.AllocHGlobal(probeSize);
                if (!DeviceIoControl(volume, IoctlVolumeGetVolumeDiskExtents, IntPtr.Zero, 0, buffer, (uint)probeSize, out _, IntPtr.Zero))
                {
                    return NativeAnswer.Failed<uint[]>(
                        Describe("The USB volume's disk extents could not be read", Marshal.GetLastPInvokeError()));
                }
            }

            var extents = Marshal.ReadInt32(buffer);
            if (extents <= 0)
            {
                return NativeAnswer.Failed<uint[]>("The USB volume reported no disk extents.");
            }

            var disks = new uint[extents];
            for (var index = 0; index < extents; index++)
            {
                disks[index] = (uint)Marshal.ReadInt32(buffer, headerSize + (index * extentSize));
            }

            return NativeAnswer.Ok<uint[]>(disks);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Some devices do not answer the extents query at all. The device number is a weaker
    /// answer — it cannot report spanning — so it is used only when the extents query
    /// failed for a reason other than buffer size, and it is still a real answer or a real
    /// failure.
    /// </summary>
    private static NativeAnswer<uint[]> ReadSingleDiskNumberFallback(SafeFileHandle volume, int extentsError)
    {
        var size = Marshal.SizeOf<StorageDeviceNumber>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(volume, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, buffer, (uint)size, out _, IntPtr.Zero))
            {
                return NativeAnswer.Failed<uint[]>(
                    Describe("The USB disk number could not be read", Marshal.GetLastPInvokeError())
                    + Describe("; the extent query also failed", extentsError));
            }

            var number = Marshal.PtrToStructure<StorageDeviceNumber>(buffer).DeviceNumber;
            return NativeAnswer.Ok<uint[]>([(uint)number]);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static NativeAnswer<bool> ReadRemovableMedia(SafeFileHandle volume) =>
        ReadDeviceDescriptor(volume, descriptor => descriptor.RemovableMedia);

    private static NativeAnswer<int> ReadBusType(SafeFileHandle volume) =>
        ReadDeviceDescriptor(volume, descriptor => descriptor.BusType);

    /// <summary>
    /// <c>STORAGE_DEVICE_DESCRIPTOR</c> is variable length; its <c>Size</c> field gives the
    /// real length, so the header is read first and the full descriptor second.
    /// </summary>
    private static NativeAnswer<T> ReadDeviceDescriptor<T>(
        SafeFileHandle volume,
        Func<StorageDeviceDescriptor, T> select)
    {
        var query = new StoragePropertyQuery { PropertyId = 0, QueryType = 0 };
        var querySize = Marshal.SizeOf<StoragePropertyQuery>();
        var queryBuffer = Marshal.AllocHGlobal(querySize);
        var headerSize = Marshal.SizeOf<StorageDescriptorHeader>();
        var headerBuffer = Marshal.AllocHGlobal(headerSize);
        var descriptorBuffer = IntPtr.Zero;
        try
        {
            Marshal.StructureToPtr(query, queryBuffer, false);
            if (!DeviceIoControl(volume, IoctlStorageQueryProperty, queryBuffer, (uint)querySize, headerBuffer, (uint)headerSize, out _, IntPtr.Zero))
            {
                return NativeAnswer.Failed<T>(
                    Describe("The USB disk properties could not be read", Marshal.GetLastPInvokeError()));
            }

            var header = Marshal.PtrToStructure<StorageDescriptorHeader>(headerBuffer);
            var total = (int)Math.Max(header.Size, (uint)Marshal.SizeOf<StorageDeviceDescriptor>());
            descriptorBuffer = Marshal.AllocHGlobal(total);
            if (!DeviceIoControl(volume, IoctlStorageQueryProperty, queryBuffer, (uint)querySize, descriptorBuffer, (uint)total, out _, IntPtr.Zero))
            {
                return NativeAnswer.Failed<T>(
                    Describe("The USB disk descriptor could not be read", Marshal.GetLastPInvokeError()));
            }

            return NativeAnswer.Ok<T>(select(Marshal.PtrToStructure<StorageDeviceDescriptor>(descriptorBuffer)));
        }
        finally
        {
            Marshal.FreeHGlobal(queryBuffer);
            Marshal.FreeHGlobal(headerBuffer);
            if (descriptorBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(descriptorBuffer);
            }
        }
    }

    public NativeAnswer<uint> SystemDiskNumber()
    {
        string? root;
        try
        {
            root = Path.GetPathRoot(Environment.SystemDirectory);
        }
        catch (ArgumentException exception)
        {
            return NativeAnswer.Failed<uint>(exception.Message);
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            return NativeAnswer.Failed<uint>("The Windows system drive could not be determined.");
        }

        using var handle = OpenDevice(@"\\.\" + root.TrimEnd('\\'));
        if (handle.IsInvalid)
        {
            return NativeAnswer.Failed<uint>(
                Describe("The Windows system volume could not be opened", Marshal.GetLastPInvokeError()));
        }

        var disks = ReadDiskNumbers(handle);
        if (!disks.Succeeded || disks.Value is not { Length: > 0 } numbers)
        {
            return NativeAnswer.Failed<uint>(disks.Error ?? "The Windows system disk could not be identified.");
        }

        return NativeAnswer.Ok<uint>(numbers[0]);
    }

    public NativeAnswer<DiskDevnode[]> EnumeratePresentDisks()
    {
        var interfaceGuid = DiskInterface;
        var set = SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1))
        {
            return NativeAnswer.Failed<DiskDevnode[]>(
                Describe("Disk devices could not be enumerated", Marshal.GetLastPInvokeError()));
        }

        try
        {
            var found = new List<DiskDevnode>();
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref interfaceData))
                {
                    break;
                }

                var info = new SpDevinfoData { CbSize = (uint)Marshal.SizeOf<SpDevinfoData>() };
                var path = ReadInterfacePath(set, ref interfaceData, ref info);
                if (path is null)
                {
                    continue;
                }

                var instanceId = InstanceId(info.DevInst);
                if (!instanceId.Succeeded || instanceId.Value is not { } id)
                {
                    return NativeAnswer.Failed<DiskDevnode[]>(
                        instanceId.Error ?? "A disk device instance could not be read.");
                }

                using var disk = OpenDevice(path);
                var number = disk.IsInvalid
                    ? NativeAnswer.Failed<uint>(
                        Describe($"The disk at {path} could not be opened", Marshal.GetLastPInvokeError()))
                    : SingleDiskNumber(disk);
                found.Add(new DiskDevnode(info.DevInst, id, number));
            }

            return NativeAnswer.Ok<DiskDevnode[]>([.. found]);
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static NativeAnswer<uint> SingleDiskNumber(SafeFileHandle disk)
    {
        var disks = ReadDiskNumbers(disk);
        if (!disks.Succeeded || disks.Value is not { Length: > 0 } numbers)
        {
            return NativeAnswer.Failed<uint>(disks.Error ?? "The disk number could not be read.");
        }

        return NativeAnswer.Ok<uint>(numbers[0]);
    }

    public NativeAnswer<uint> Parent(uint devInst)
    {
        var status = CM_Get_Parent(out var parent, devInst, 0);
        return status switch
        {
            CrSuccess => NativeAnswer.Ok<uint>(parent),
            CrNoSuchDevnode => NativeAnswer.Absent<uint>(),
            _ => NativeAnswer.Failed<uint>(DescribeConfigRet("The parent device could not be read", status))
        };
    }

    /// <summary>
    /// Reads an instance id, sizing the buffer from <c>CM_Get_Device_ID_Size</c> rather
    /// than assuming one.
    /// </summary>
    public NativeAnswer<string> InstanceId(uint devInst)
    {
        var sizeStatus = CM_Get_Device_ID_Size(out var length, devInst, 0);
        if (sizeStatus != CrSuccess)
        {
            return NativeAnswer.Failed<string>(DescribeConfigRet("The device instance size could not be read", sizeStatus));
        }

        // The reported length excludes the terminator.
        var buffer = new char[length + 1];
        var status = CM_Get_Device_IDW(devInst, ref AsUtf16(buffer), (uint)buffer.Length, 0);
        if (status != CrSuccess)
        {
            return NativeAnswer.Failed<string>(DescribeConfigRet("The device instance could not be read", status));
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return NativeAnswer.Ok<string>(new string(buffer, 0, terminator >= 0 ? terminator : buffer.Length));
    }

    /// <summary>
    /// Every child of a node.
    /// </summary>
    /// <remarks>
    /// <c>CR_NO_SUCH_DEVNODE</c> from <c>CM_Get_Child</c> or <c>CM_Get_Sibling</c> means
    /// the walk ended. Any other status means the walk could not be completed, and that is
    /// reported as a failure — not as "there are no more children", which is how a hub
    /// with several devices could otherwise pass for a device with one.
    /// </remarks>
    public NativeAnswer<uint[]> Children(uint devInst)
    {
        var status = CM_Get_Child(out var child, devInst, 0);
        if (status == CrNoSuchDevnode)
        {
            return NativeAnswer.Ok<uint[]>([]);
        }

        if (status != CrSuccess)
        {
            return NativeAnswer.Failed<uint[]>(DescribeConfigRet("The device's children could not be read", status));
        }

        var children = new List<uint> { child };
        for (var guard = 0; guard < 4096; guard++)
        {
            var siblingStatus = CM_Get_Sibling(out var sibling, children[^1], 0);
            if (siblingStatus == CrNoSuchDevnode)
            {
                return NativeAnswer.Ok<uint[]>([.. children]);
            }

            if (siblingStatus != CrSuccess)
            {
                return NativeAnswer.Failed<uint[]>(
                    DescribeConfigRet("The device's remaining children could not be read", siblingStatus));
            }

            children.Add(sibling);
        }

        return NativeAnswer.Failed<uint[]>("The device reported an implausible number of children.");
    }

    private static SafeFileHandle OpenDevice(string path) =>
        CreateFileW(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    private static string? ReadInterfacePath(IntPtr set, ref SpDeviceInterfaceData interfaceData, ref SpDevinfoData info)
    {
        SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0)
        {
            return null;
        }

        var detail = Marshal.AllocHGlobal((int)required);
        try
        {
            // cbSize is the size of the fixed part of the struct, not of the buffer:
            // 8 on 64-bit (DWORD plus WCHAR[1] padded to pointer alignment), 6 on 32-bit.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            return SetupDiGetDeviceInterfaceDetailW(set, ref interfaceData, detail, required, out _, ref info)
                ? Marshal.PtrToStringUni(detail + 4)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    private static string Describe(string what, int error) =>
        $"{what} (Windows error {error.ToString(CultureInfo.InvariantCulture)}): {new Win32Exception(error).Message}";

    /// <summary>
    /// A CONFIGRET is not a Win32 error code and must not be formatted as one.
    /// </summary>
    private static string DescribeConfigRet(string what, int status) =>
        $"{what} (CONFIGRET 0x{status.ToString("X8", CultureInfo.InvariantCulture)}).";

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDescriptorHeader
    {
        public uint Version;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceDescriptor
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        [MarshalAs(UnmanagedType.U1)] public bool RemovableMedia;
        [MarshalAs(UnmanagedType.U1)] public bool CommandQueueing;
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public int BusType;
        public uint RawPropertiesLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

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

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData interfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData interfaceData,
        IntPtr detailData,
        uint detailSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData interfaceData,
        IntPtr detailData,
        uint detailSize,
        out uint requiredSize,
        ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Child(out uint child, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial int CM_Get_Sibling(out uint sibling, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_IDW")]
    private static partial int CM_Get_Device_IDW(uint devInst, ref ushort buffer, uint bufferLength, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_Size")]
    private static partial int CM_Get_Device_ID_Size(out uint length, uint devInst, uint flags);
}
