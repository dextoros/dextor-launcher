using DexTor.SessionHost;

namespace DexTor.Launcher.Tests;

/// <summary>
/// A recorded device topology. Every answer is explicit, including the failures, so a
/// test can say "this query did not work" as distinct from "there is nothing there".
/// </summary>
internal sealed class FixtureTopology : INativeDeviceTopology
{
    // Device instance numbers are opaque handles; these stand in for real ones.
    public const uint HostController = 10;
    public const uint Hub = 20;
    public const uint UsbDevice = 30;
    public const uint DiskNode = 40;
    public const uint SecondDiskNode = 41;

    public const string VolumeGuid = @"\\?\Volume{aaaaaaaa-1111-2222-3333-444444444444}\";

    /// <summary>An ordinary USB stick: disk under USBSTOR, parent a plain USB device.</summary>
    public static FixtureTopology OrdinaryStick() => new()
    {
        Volume = VolumeGuid,
        Disks = [2],
        SystemDisk = 0,
        BusType = 0x07,
        RemovableMedia = true,
        PresentDisks =
        [
            new DiskDevnode(DiskNode, @"USBSTOR\DISK&VEN_DEXTOR&PROD_USB&REV_1.00\0123456789&0", NativeAnswer.Ok<uint>(2))
        ],
        Parents = { [DiskNode] = UsbDevice, [UsbDevice] = Hub, [Hub] = HostController },
        InstanceIds =
        {
            [DiskNode] = @"USBSTOR\DISK&VEN_DEXTOR&PROD_USB&REV_1.00\0123456789&0",
            [UsbDevice] = @"USB\VID_1234&PID_5678\0123456789",
            [Hub] = @"USB\VID_8087&PID_0024\5&1a2b3c4d&0",
            [HostController] = @"PCI\VEN_8086&DEV_A12F\3&11583659&0&A0"
        },
        ChildMap = { [UsbDevice] = [DiskNode], [Hub] = [UsbDevice], [HostController] = [Hub] }
    };

    public string Volume { get; set; } = string.Empty;

    /// <summary>
    /// Volumes for paths that are not the session's storage, keyed by path prefix.
    /// </summary>
    /// <remarks>
    /// A machine has more than one volume, and a diagnostic that writes its report to host
    /// storage has to be able to tell that volume from the one it is inspecting. Without
    /// this every path in a fixture would answer with the same volume, which is exactly the
    /// case the report destination guard exists to catch.
    /// </remarks>
    public Dictionary<string, string> VolumesByPathPrefix { get; } = [];

    public string? VolumeFailure { get; set; }

    public uint[] Disks { get; set; } = [];

    public uint SystemDisk { get; set; }

    public string? SystemDiskFailure { get; set; }

    public int BusType { get; set; }

    public bool RemovableMedia { get; set; }

    public string? PropertyFailure { get; set; }

    public DiskDevnode[] PresentDisks { get; set; } = [];

    public string? EnumerationFailure { get; set; }

    public Dictionary<uint, uint> Parents { get; } = [];

    public string? ParentFailure { get; set; }

    public Dictionary<uint, string> InstanceIds { get; } = [];

    public Dictionary<uint, uint[]> ChildMap { get; } = [];

    public string? ChildrenFailure { get; set; }

    public NativeAnswer<string> VolumeGuidPathFor(string path)
    {
        foreach (var (prefix, volume) in VolumesByPathPrefix)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return NativeAnswer.Ok(volume);
            }
        }

        return VolumeFailure is not null
            ? NativeAnswer.Failed<string>(VolumeFailure)
            : NativeAnswer.Ok(Volume);
    }

    public VolumeStorageFacts QueryVolume(string volumeGuidPath) =>
        new(
            NativeAnswer.Ok(Disks),
            PropertyFailure is not null
                ? NativeAnswer.Failed<bool>(PropertyFailure)
                : NativeAnswer.Ok(RemovableMedia),
            PropertyFailure is not null
                ? NativeAnswer.Failed<int>(PropertyFailure)
                : NativeAnswer.Ok(BusType));

    public NativeAnswer<uint> SystemDiskNumber() =>
        SystemDiskFailure is not null
            ? NativeAnswer.Failed<uint>(SystemDiskFailure)
            : NativeAnswer.Ok(SystemDisk);

    public NativeAnswer<DiskDevnode[]> EnumeratePresentDisks() =>
        EnumerationFailure is not null
            ? NativeAnswer.Failed<DiskDevnode[]>(EnumerationFailure)
            : NativeAnswer.Ok(PresentDisks);

    public NativeAnswer<uint> Parent(uint devInst) =>
        ParentFailure is not null
            ? NativeAnswer.Failed<uint>(ParentFailure)
            : Parents.TryGetValue(devInst, out var parent)
                ? NativeAnswer.Ok(parent)
                : NativeAnswer.Absent<uint>();

    public NativeAnswer<string> InstanceId(uint devInst) =>
        InstanceIds.TryGetValue(devInst, out var id)
            ? NativeAnswer.Ok(id)
            : NativeAnswer.Failed<string>($"No instance id recorded for {devInst}.");

    public NativeAnswer<uint[]> Children(uint devInst) =>
        ChildrenFailure is not null
            ? NativeAnswer.Failed<uint[]>(ChildrenFailure)
            : ChildMap.TryGetValue(devInst, out var children)
                ? NativeAnswer.Ok(children)
                : NativeAnswer.Ok<uint[]>([]);
}
