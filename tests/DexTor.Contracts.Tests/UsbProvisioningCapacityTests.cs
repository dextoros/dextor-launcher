using FluentAssertions;

using Xunit;

namespace DexTor.Contracts.Tests;

public sealed class UsbProvisioningCapacityTests
{
    [Fact]
    public void MarketedSixteenGbDriveFitsCurrentLiveImageAndMinimumPrivateStorage()
    {
        const long marketedSixteenGbDrive = 15_500_000_000;
        const long currentLiveImage = 2_845_000_000;

        var fits = UsbProvisioningCapacity.TryCalculateDataImageSize(
            marketedSixteenGbDrive,
            currentLiveImage,
            out var dataImageSize);

        fits.Should().BeTrue();
        dataImageSize.Should().BeGreaterThanOrEqualTo(UsbProvisioningCapacity.MinimumDataImageBytes);
    }

    [Fact]
    public void MarketedEightGbDriveIsTheSupportedMinimumForCurrentLiveImage()
    {
        const long marketedEightGbDrive = 7_800_000_000;
        const long currentLiveImage = 2_845_000_000;

        var fits = UsbProvisioningCapacity.TryCalculateDataImageSize(
            marketedEightGbDrive,
            currentLiveImage,
            out var dataImageSize);

        fits.Should().BeTrue();
        dataImageSize.Should().BeGreaterThanOrEqualTo(UsbProvisioningCapacity.MinimumDataImageBytes);
    }

    [Fact]
    public void DriveIsRejectedWhenDuplicatedSystemImageAndMinimumStorageDoNotFit()
    {
        const long imageSize = 3L * 1024 * 1024 * 1024;
        var required = imageSize * 2
            + UsbProvisioningCapacity.MetadataAllowanceBytes
            + UsbProvisioningCapacity.LayoutSafetyBytes
            + UsbProvisioningCapacity.MinimumDataImageBytes;

        UsbProvisioningCapacity.TryCalculateDataImageSize(required - 1, imageSize, out _).Should().BeFalse();
        UsbProvisioningCapacity.TryCalculateDataImageSize(required, imageSize, out var dataSize).Should().BeTrue();
        dataSize.Should().Be(UsbProvisioningCapacity.MinimumDataImageBytes);
    }

    [Fact]
    public void PrivateStorageTakesHalfOfTheRoomLeftAfterTheSystemSoTheRestStaysFree()
    {
        const long sixtyFourGbDrive = 62_008_590_336; // Kingston DataTraveler 3.0, 57.75 GiB
        const long currentLiveImage = 2_047_825_920;
        var afterSystem = sixtyFourGbDrive - (currentLiveImage * 2
            + UsbProvisioningCapacity.MetadataAllowanceBytes
            + UsbProvisioningCapacity.LayoutSafetyBytes);

        UsbProvisioningCapacity.TryCalculateDataImageSize(sixtyFourGbDrive, currentLiveImage, out var dataSize)
            .Should().BeTrue();

        dataSize.Should().BeLessThanOrEqualTo(afterSystem / 2);
        dataSize.Should().BeGreaterThan(afterSystem / 2 - 1024 * 1024);
        (dataSize % (1024 * 1024)).Should().Be(0, "the file is sized in whole mebibytes");
    }

    [Fact]
    public void SmallDriveStillGetsTheMinimumStorageOutOfTheFreeHalf()
    {
        const long imageSize = 3L * 1024 * 1024 * 1024;
        var fixedBytes = imageSize * 2
            + UsbProvisioningCapacity.MetadataAllowanceBytes
            + UsbProvisioningCapacity.LayoutSafetyBytes;
        var capacity = fixedBytes + UsbProvisioningCapacity.MinimumDataImageBytes + 512L * 1024 * 1024;

        UsbProvisioningCapacity.TryCalculateDataImageSize(capacity, imageSize, out var dataSize).Should().BeTrue();

        dataSize.Should().Be(UsbProvisioningCapacity.MinimumDataImageBytes);
    }
}
