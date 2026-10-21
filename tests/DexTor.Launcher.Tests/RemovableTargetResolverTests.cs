using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// The production resolver algorithm, driven by recorded native answers.
/// </summary>
/// <remarks>
/// <see cref="RemovableTargetResolver"/> is the same type
/// <see cref="WindowsRemovableDeviceResolver"/> runs; only
/// <see cref="INativeDeviceTopology"/> is substituted, so what is under test is the real
/// decision chain — storage identity, volume, disk, disk device node, USB storage
/// function, validated target — and not a preselected answer.
/// </remarks>
public sealed class RemovableTargetResolverTests
{
    private const string ImagePath = @"E:\DEXTOR\storage\data.img";
    private const string Volume = FixtureTopology.VolumeGuid;
    private const uint HostController = FixtureTopology.HostController;
    private const uint Hub = FixtureTopology.Hub;
    private const uint UsbDevice = FixtureTopology.UsbDevice;
    private const uint DiskNode = FixtureTopology.DiskNode;
    private const uint SecondDiskNode = FixtureTopology.SecondDiskNode;

    private static FixtureTopology OrdinaryStick() => FixtureTopology.OrdinaryStick();

    [Fact]
    public void AnOrdinaryUsbStickResolvesToItsUsbStorageDevice()
    {
        var resolution = new RemovableTargetResolver(OrdinaryStick()).Resolve(ImagePath);

        resolution.Resolved.Should().BeTrue(resolution.Reason);

        // The target is the USB storage device — one step up from the disk — never the hub
        // above it and never the disk node itself.
        resolution.Target!.DeviceInstanceId.Should().Be(@"USB\VID_1234&PID_5678\0123456789");
        resolution.Target.VolumeGuidPath.Should().Be(Volume);
    }

    [Fact]
    public void UaspStorageResolvesTheSameWay()
    {
        // UASP disks are still enumerated under USBSTOR by Windows; the service behind
        // them differs, the topology does not.
        var topology = OrdinaryStick();
        topology.PresentDisks =
        [
            new DiskDevnode(DiskNode, @"USBSTOR\DISK&VEN_SEAGATE&PROD_UASP&REV_0100\NA9XYZ&0", NativeAnswer.Ok<uint>(2))
        ];
        topology.InstanceIds[DiskNode] = @"USBSTOR\DISK&VEN_SEAGATE&PROD_UASP&REV_0100\NA9XYZ&0";

        new RemovableTargetResolver(topology).Resolve(ImagePath).Resolved.Should().BeTrue();
    }

    [Fact]
    public void AStickBehindAHubStillTargetsItsOwnDeviceAndNotTheHub()
    {
        // The hub carries a second, unrelated device. Nothing about resolution may widen
        // to the hub, because removing it would take that device too.
        var topology = OrdinaryStick();
        topology.ChildMap[Hub] = [UsbDevice, 31];
        topology.InstanceIds[31] = @"USB\VID_046D&PID_C52B\5&2b3c4d5e&0";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeTrue(resolution.Reason);
        resolution.Target!.DeviceInstanceId.Should().Be(@"USB\VID_1234&PID_5678\0123456789");
    }

    [Fact]
    public void AHubIsRefusedEvenWhenItHoldsExactlyOneDevice()
    {
        // "One child" is a guard, not the evidence. A hub with a single device attached
        // satisfies it, so the enumerator of the disk node is what decides.
        var topology = OrdinaryStick();
        topology.Parents[DiskNode] = Hub;
        topology.ChildMap[Hub] = [DiskNode];
        topology.PresentDisks =
        [
            new DiskDevnode(DiskNode, @"USB\VID_8087&PID_0024\5&1a2b3c4d&0", NativeAnswer.Ok<uint>(2))
        ];
        topology.InstanceIds[DiskNode] = @"USB\VID_8087&PID_0024\5&1a2b3c4d&0";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("not USB mass storage");
    }

    [Fact]
    public void AHostControllerParentIsRefused()
    {
        var topology = OrdinaryStick();
        topology.Parents[DiskNode] = HostController;
        topology.ChildMap[HostController] = [DiskNode];

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("not a USB device");
        resolution.Reason.Should().NotContain("0123456789", "an operator message need not carry the device serial");
    }

    [Fact]
    public void ACompositeDeviceFunctionIsRefusedRatherThanClimbedPast()
    {
        // The storage is one interface of a composite device. Ejecting the interface is
        // wrong, and climbing to the whole device would take the other functions with it.
        var topology = OrdinaryStick();
        topology.InstanceIds[UsbDevice] = @"USB\VID_1234&PID_5678&MI_00\6&1a2b3c4d&0&0000";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("composite device");
    }

    [Fact]
    public void AMultiLunEnclosureIsRefusedBecauseItsDeviceHoldsMoreThanOurDisk()
    {
        // Two LUNs, two disk nodes, one USB device. Removing it would take the other LUN.
        var topology = OrdinaryStick();
        topology.ChildMap[UsbDevice] = [DiskNode, SecondDiskNode];
        topology.InstanceIds[SecondDiskNode] = @"USBSTOR\DISK&VEN_DEXTOR&PROD_USB&REV_1.00\0123456789&1";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("holds 2 devices");
    }

    [Fact]
    public void AVolumeSpanningTwoDisksIsRefused()
    {
        var topology = OrdinaryStick();
        topology.Disks = [2, 3];

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("spans 2 disks");
    }

    [Fact]
    public void TheWindowsSystemDiskIsRefused()
    {
        var topology = OrdinaryStick();
        topology.SystemDisk = 2;

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("system disk");
    }

    [Fact]
    public void ANonUsbBusIsRefused()
    {
        var topology = OrdinaryStick();
        topology.BusType = 0x0B; // BusTypeiScsi

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("not on a USB disk");
    }

    // ------------------------------------------- native failure is never a negative answer

    [Fact]
    public void AFailedChildQueryIsNotReadAsHavingNoOtherChildren()
    {
        // This is the defect the boundary exists to expose: a CONFIGRET that is not
        // CR_NO_SUCH_DEVNODE means the walk could not be completed, and a hub full of
        // devices must not pass for a device with one.
        var topology = OrdinaryStick();
        topology.ChildrenFailure = "CONFIGRET 0x00000013";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("contents could not be read");
    }

    [Fact]
    public void AFailedParentQueryIsNotReadAsHavingNoParent()
    {
        var topology = OrdinaryStick();
        topology.ParentFailure = "CONFIGRET 0x00000013";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("parent device could not be read");
    }

    [Fact]
    public void AnAbsentParentIsDistinguishedFromAFailedQuery()
    {
        var topology = OrdinaryStick();
        topology.Parents.Remove(DiskNode);

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("no parent device");
    }

    [Fact]
    public void AFailedPropertyQueryIsNotReadAsNotRemovable()
    {
        var topology = OrdinaryStick();
        topology.PropertyFailure = "The USB disk properties could not be read.";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("properties could not be read");
    }

    [Fact]
    public void AnUnknownSystemDiskRefusesEveryRemoval()
    {
        // Not knowing which disk Windows runs from is not a reason to proceed.
        var topology = OrdinaryStick();
        topology.SystemDiskFailure = "The Windows system volume could not be opened.";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("which disk Windows runs from");
    }

    [Fact]
    public void ADiskWhoseNumberCannotBeReadStopsTheWholeResolution()
    {
        // One unreadable disk among the present devices means the match cannot be trusted,
        // so no device is chosen at all.
        var topology = OrdinaryStick();
        topology.PresentDisks =
        [
            new DiskDevnode(SecondDiskNode, @"SCSI\DISK&VEN_NVME\4&1", NativeAnswer.Failed<uint>("unreadable")),
            .. topology.PresentDisks
        ];

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("could not be identified");
    }

    [Fact]
    public void TwoDevicesClaimingTheSameDiskNumberAreRefused()
    {
        var topology = OrdinaryStick();
        topology.PresentDisks =
        [
            .. topology.PresentDisks,
            new DiskDevnode(SecondDiskNode, @"USBSTOR\DISK&VEN_OTHER\9&0", NativeAnswer.Ok<uint>(2))
        ];

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("More than one present device");
    }

    // ------------------------------------------------ identity, not letters or numbers

    [Fact]
    public void ThePathIsRefusedWhenItNoLongerResolvesToTheSessionVolume()
    {
        // The session started on one volume; the drive letter now answers for another.
        // Resolution is bound to what the session took, not to what the path says today.
        var topology = OrdinaryStick();
        topology.Volume = @"\\?\Volume{bbbbbbbb-9999-8888-7777-666666666666}\";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath, Volume);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("no longer the volume");
    }

    [Fact]
    public void TheSameVolumeIsAcceptedRegardlessOfTrailingSeparator()
    {
        var resolution = new RemovableTargetResolver(OrdinaryStick())
            .Resolve(ImagePath, Volume.TrimEnd('\\'));

        resolution.Resolved.Should().BeTrue(resolution.Reason);
    }

    [Fact]
    public void ADeviceThatDisappearedDuringFinalizationIsRefused()
    {
        var topology = OrdinaryStick();
        topology.PresentDisks = [];

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("No present disk matched");
    }

    [Fact]
    public void AVolumeThatCannotBeResolvedIsRefused()
    {
        var topology = OrdinaryStick();
        topology.VolumeFailure = "The path is not on a resolvable local volume.";

        var resolution = new RemovableTargetResolver(topology).Resolve(ImagePath);

        resolution.Resolved.Should().BeFalse();
        resolution.Reason.Should().Contain("resolvable local volume");
    }
}
