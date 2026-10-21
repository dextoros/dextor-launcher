using DexTor.Contracts;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class UsbProvisioningPlanTests
{
    [Fact]
    public void BuildsFixedHybridGptLayoutForVerifiedUsb()
    {
        var request = CreateRequest();
        var actual = CreateActualDisk();

        var plan = UsbProvisioningPlanner.Build(request, actual, 'Z');

        plan.DiskNumber.Should().Be(7);
        plan.PrepareDiskScript.Should().Contain("select disk 7");
        plan.PrepareDiskScript.Should().Contain("clean");
        plan.PrepareDiskScript.IndexOf("clean", StringComparison.Ordinal)
            .Should().BeLessThan(plan.PrepareDiskScript.IndexOf("convert gpt", StringComparison.Ordinal),
                "a cleaned stick must not be left without a partition table, or Windows mounts the whole disk");
        plan.PrepareDiskScript.Should().NotContain("online disk");
        plan.PrepareDiskScript.Should().NotContain("offline disk");
        plan.FormatDataPartitionScript.Should().NotContain("online disk");
        plan.FormatDataPartitionScript.Should().Contain("select partition 5");
        plan.FormatDataPartitionScript.Should().Contain("format quick fs=ntfs label=DEXTOR");
        plan.FormatDataPartitionScript.IndexOf("select partition 5", StringComparison.Ordinal)
            .Should().BeLessThan(plan.FormatDataPartitionScript.IndexOf("format quick", StringComparison.Ordinal));
        plan.FormatDataPartitionScript.Should().NotContain("assign", "the volume gets no letter while it is being written");
        plan.FormatDataPartitionScript.Should().NotContain("gpt attributes", "no default drive letter stays set while writing");
        plan.FinalizeDataPartitionScript.Should().Contain("select disk 7");
        plan.FinalizeDataPartitionScript.IndexOf("select partition 5", StringComparison.Ordinal)
            .Should().BeLessThan(plan.FinalizeDataPartitionScript.IndexOf("gpt attributes=0x0000000000000000", StringComparison.Ordinal));
        plan.FinalizeDataPartitionScript.IndexOf("gpt attributes=0x0000000000000000", StringComparison.Ordinal)
            .Should().BeLessThan(plan.FinalizeDataPartitionScript.IndexOf("assign letter=Z", StringComparison.Ordinal));
        plan.FormatDataPartitionScript.Should().NotContain(request.Device.UniqueId);
        plan.FormatDataPartitionScript.Should().NotContain(request.Device.FriendlyName);
    }

    [Theory]
    [InlineData(false, false, "SATA")]
    [InlineData(true, false, "USB")]
    [InlineData(false, true, "USB")]
    public void RejectsNonUsbAndWindowsSystemDisks(bool isBoot, bool isSystem, string busType)
    {
        var actual = CreateActualDisk() with { IsBoot = isBoot, IsSystem = isSystem, BusType = busType };

        var action = () => UsbProvisioningPlanner.Build(CreateRequest(), actual, 'Z');

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RejectsChangedPhysicalIdentityAndMissingEraseConfirmation()
    {
        var changed = CreateActualDisk() with { UniqueId = "another-device" };
        var changedAction = () => UsbProvisioningPlanner.Build(CreateRequest(), changed, 'Z');
        changedAction.Should().Throw<InvalidOperationException>().WithMessage("*identity changed*");

        var unconfirmed = CreateRequest() with { ConfirmErase = false };
        var unconfirmedAction = () => UsbProvisioningPlanner.Build(unconfirmed, CreateActualDisk(), 'Z');
        unconfirmedAction.Should().Throw<InvalidOperationException>().WithMessage("*confirmation*");
    }

    [Fact]
    public void AcceptsPhysicalCapacityReportedByAMarketedSixteenGbDrive()
    {
        const long actualCapacity = 15_500_000_000;
        var actual = CreateActualDisk() with { CapacityBytes = actualCapacity };
        var request = CreateRequest() with
        {
            Device = CreateRequest().Device with { CapacityBytes = actualCapacity },
            DataImageSizeBytes = UsbProvisioningCapacity.MinimumDataImageBytes
        };

        var action = () => UsbProvisioningPlanner.ValidateRequest(request, actual);

        action.Should().NotThrow();
    }

    private static UsbProvisioningRequest CreateRequest() => new(
        Guid.NewGuid(),
        new UsbPhysicalIdentity(
            7,
            "unique-usb-id",
            "serial-123",
            64L * 1024 * 1024 * 1024,
            "USB",
            "Test USB",
            false,
            false,
            false),
        @"C:\image\dextor.iso",
        new string('a', 64),
        32L * 1024 * 1024 * 1024,
        true);

    private static PhysicalDiskSnapshot CreateActualDisk() => new(
        7,
        "unique-usb-id",
        "serial-123",
        64L * 1024 * 1024 * 1024,
        "USB",
        "Test USB",
        false,
        false,
        false,
        false);
}
