using System.Globalization;
using DexTor.Contracts;

namespace DexTor.SessionHost;

/// <summary>
/// One answer from a native query: a value, or the reason it could not be obtained.
/// </summary>
/// <remarks>
/// The distinction is the point. A query that failed is not evidence of anything — not
/// "no siblings", not "not a hub", not "no parent". Folding a failure into a negative
/// answer is how a resolver talks itself into ejecting the wrong device, so every native
/// result carries whether it actually succeeded.
/// </remarks>
/// <param name="HasValue">
/// Whether the query produced something. Kept explicit rather than inferred from a null
/// <paramref name="Value"/>, because a device instance number is a value type and "no
/// parent" would otherwise be indistinguishable from "parent zero".
/// </param>
public readonly record struct NativeAnswer<T>(bool Succeeded, bool HasValue, T Value, string? Error)
{
    /// <summary>The query ran and established that nothing is there.</summary>
    public bool IsAbsent => Succeeded && !HasValue;
}

/// <summary>Factories for <see cref="NativeAnswer{T}"/>.</summary>
public static class NativeAnswer
{
    public static NativeAnswer<T> Ok<T>(T value) => new(true, true, value, null);

    /// <summary>The query succeeded and there is nothing there.</summary>
    public static NativeAnswer<T> Absent<T>() => new(true, false, default!, null);

    public static NativeAnswer<T> Failed<T>(string? error) =>
        new(false, false, default!, error ?? "The native query failed.");
}

/// <summary>What the volume itself reports about the storage under it.</summary>
public sealed record VolumeStorageFacts(
    NativeAnswer<uint[]> DiskNumbers,
    NativeAnswer<bool> RemovableMedia,
    NativeAnswer<int> BusType);

/// <summary>A present disk device node, as device enumeration reports it.</summary>
public sealed record DiskDevnode(uint DevInst, string InstanceId, NativeAnswer<uint> DiskNumber);

/// <summary>
/// The native surface the resolver depends on, so the algorithm can be exercised against
/// recorded topologies instead of whatever hardware happens to be plugged in.
/// </summary>
public interface INativeDeviceTopology
{
    /// <summary>The <c>\\?\Volume{GUID}\</c> root for a path, or why it could not be had.</summary>
    NativeAnswer<string> VolumeGuidPathFor(string path);

    VolumeStorageFacts QueryVolume(string volumeGuidPath);

    /// <summary>The disk number carrying the Windows system directory.</summary>
    NativeAnswer<uint> SystemDiskNumber();

    NativeAnswer<DiskDevnode[]> EnumeratePresentDisks();

    NativeAnswer<uint> Parent(uint devInst);

    NativeAnswer<string> InstanceId(uint devInst);

    /// <summary>Every child of a node. Empty means "none"; a failure means "unknown".</summary>
    NativeAnswer<uint[]> Children(uint devInst);
}

/// <summary>
/// The production algorithm that turns the session's storage into one removal target.
/// </summary>
/// <remarks>
/// The chain is fixed and every link is checked:
/// <c>session storage identity → volume → disk → disk device node → USB storage
/// function → validated removal target</c>.
/// <para>
/// Nothing is identified by drive letter, disk number or friendly name alone. The volume
/// GUID is what carries identity; the disk number is only used to join the volume to a
/// device node within a single resolution, and the device node is accepted only on
/// positive evidence of what it is.
/// </para>
/// </remarks>
public sealed class RemovableTargetResolver(INativeDeviceTopology topology)
{
    private const int BusTypeUsb = 0x07;

    /// <summary>The enumerator Windows uses for USB mass storage, including UASP.</summary>
    private const string UsbStorageEnumerator = @"USBSTOR\";

    private const string UsbEnumerator = @"USB\";

    /// <summary>
    /// Resolves the target, or explains why there is none.
    /// </summary>
    /// <param name="dataImagePath">The session's selected storage.</param>
    /// <param name="expectedVolumeGuidPath">
    /// The volume identity captured when the session started. When supplied, a path that
    /// now resolves elsewhere is refused: between starting and finalizing, a drive letter
    /// can be reassigned and a different device can answer to the same path.
    /// </param>
    public TargetResolution Resolve(string dataImagePath, string? expectedVolumeGuidPath = null)
    {
        var volumeAnswer = topology.VolumeGuidPathFor(dataImagePath);
        if (!volumeAnswer.Succeeded || volumeAnswer.Value is not { } volume)
        {
            return Refuse(volumeAnswer.Error ?? "The persistent image is not on a resolvable local volume.");
        }

        if (expectedVolumeGuidPath is not null
            && !string.Equals(Normalize(volume), Normalize(expectedVolumeGuidPath), StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                "The storage this session was started from is no longer the volume behind that path, "
                + "so DexTor will not ask Windows to remove it.");
        }

        var facts = topology.QueryVolume(volume);
        if (!facts.DiskNumbers.Succeeded || facts.DiskNumbers.Value is not { } disks)
        {
            return Refuse(facts.DiskNumbers.Error ?? "The disks behind the USB volume could not be read.");
        }

        if (disks.Length != 1)
        {
            return Refuse(
                $"The USB volume spans {disks.Length.ToString(CultureInfo.InvariantCulture)} disks, "
                + "so DexTor will not choose one to remove.");
        }

        var diskNumber = disks[0];

        // The system disk is refused before anything else is asked about it.
        var systemDisk = topology.SystemDiskNumber();
        if (!systemDisk.Succeeded)
        {
            return Refuse(
                "DexTor could not determine which disk Windows runs from, so it will not remove any disk. "
                + systemDisk.Error);
        }

        if (systemDisk.Value == diskNumber)
        {
            return Refuse("The persistent image is on the Windows system disk; it will never be ejected.");
        }

        if (!IsRemovableUsbStorage(facts, out var storageReason))
        {
            return Refuse(storageReason);
        }

        return ResolveDeviceInstance(diskNumber, volume);
    }

    private static bool IsRemovableUsbStorage(VolumeStorageFacts facts, out string reason)
    {
        // Both queries must have answered. A failed property query is not evidence that
        // the disk is fixed, and it is certainly not evidence that it is removable.
        if (!facts.RemovableMedia.Succeeded || !facts.BusType.Succeeded)
        {
            reason = "The USB disk's properties could not be read, so DexTor will not ask Windows to remove it. "
                + (facts.RemovableMedia.Error ?? facts.BusType.Error);
            return false;
        }

        if (facts.BusType.Value != BusTypeUsb)
        {
            reason = "The persistent image is not on a USB disk, so DexTor will not ask Windows to remove it.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Joins the disk to its device node, then takes exactly one step to the USB storage
    /// function Windows ejects.
    /// </summary>
    /// <remarks>
    /// One step, and only onto positive evidence. The disk node must be enumerated under
    /// <c>USBSTOR\</c> — that is what makes it USB mass storage rather than something that
    /// merely happens to sit on a USB bus — and its parent must be a plain <c>USB\</c>
    /// device. A hub, a host controller and any other ancestor are therefore excluded by
    /// construction rather than by hoping the tree is shaped the expected way.
    /// <para>
    /// The parent must also hold nothing but this disk. That is a second guard, not the
    /// evidence: a hub with one device attached would satisfy "one child" and is rejected
    /// by the enumerator check above.
    /// </para>
    /// </remarks>
    private TargetResolution ResolveDeviceInstance(uint diskNumber, string volumeGuidPath)
    {
        var disksAnswer = topology.EnumeratePresentDisks();
        if (!disksAnswer.Succeeded || disksAnswer.Value is not { } disks)
        {
            return Refuse(disksAnswer.Error ?? "Disk devices could not be enumerated.");
        }

        var matches = new List<DiskDevnode>();
        foreach (var disk in disks)
        {
            if (!disk.DiskNumber.Succeeded)
            {
                // Unknown, so it cannot be excluded and it cannot be selected either.
                return Refuse(
                    $"A present disk could not be identified ({disk.DiskNumber.Error}), so DexTor will not "
                    + "choose between them.");
            }

            if (disk.DiskNumber.Value == diskNumber)
            {
                matches.Add(disk);
            }
        }

        if (matches.Count == 0)
        {
            return Refuse($"No present disk matched disk {diskNumber.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (matches.Count > 1)
        {
            return Refuse(
                $"More than one present device claims disk {diskNumber.ToString(CultureInfo.InvariantCulture)}.");
        }

        var diskNode = matches[0];
        if (!diskNode.InstanceId.StartsWith(UsbStorageEnumerator, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                $"The disk is not USB mass storage ({Describe(diskNode.InstanceId)}), so DexTor will not "
                + "ask Windows to remove it.");
        }

        var parentAnswer = topology.Parent(diskNode.DevInst);
        if (!parentAnswer.Succeeded)
        {
            return Refuse($"The USB disk's parent device could not be read ({parentAnswer.Error}).");
        }

        if (parentAnswer.IsAbsent)
        {
            return Refuse("The USB disk has no parent device to remove.");
        }

        var parent = parentAnswer.Value;
        var parentIdAnswer = topology.InstanceId(parent);
        if (!parentIdAnswer.Succeeded || parentIdAnswer.Value is not { } parentId)
        {
            return Refuse($"The USB device instance could not be read ({parentIdAnswer.Error}).");
        }

        if (!parentId.StartsWith(UsbEnumerator, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                $"The USB disk's parent is not a USB device ({Describe(parentId)}), so DexTor will not "
                + "ask Windows to remove it.");
        }

        // A composite device's storage function is one interface of several. Ejecting the
        // interface is wrong and climbing to the whole device would take the other
        // functions with it, so this is refused rather than widened.
        if (parentId.Contains("&MI_", StringComparison.OrdinalIgnoreCase))
        {
            return Refuse(
                $"The USB storage is one function of a composite device ({Describe(parentId)}), so DexTor "
                + "will not ask Windows to remove it.");
        }

        var childrenAnswer = topology.Children(parent);
        if (!childrenAnswer.Succeeded || childrenAnswer.Value is not { } children)
        {
            return Refuse($"The USB device's contents could not be read ({childrenAnswer.Error}).");
        }

        if (children.Length != 1 || children[0] != diskNode.DevInst)
        {
            return Refuse(
                $"The USB device holds {children.Length.ToString(CultureInfo.InvariantCulture)} devices, so "
                + "DexTor will not ask Windows to remove it.");
        }

        return new TargetResolution(new RemovableTarget(volumeGuidPath, parentId), null);
    }

    private static TargetResolution Refuse(string? reason) =>
        new(null, reason ?? "The USB device could not be identified.");

    /// <summary>
    /// Shows the enumerator and hardware part of an instance id without its serial, which
    /// is device-identifying and does not belong in an operator message.
    /// </summary>
    private static string Describe(string instanceId)
    {
        var lastSeparator = instanceId.LastIndexOf('\\');
        return lastSeparator > 0 ? instanceId[..lastSeparator] : instanceId;
    }

    private static string Normalize(string volumeGuidPath) => volumeGuidPath.TrimEnd('\\');
}
