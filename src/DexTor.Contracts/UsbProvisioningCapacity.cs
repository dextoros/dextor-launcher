namespace DexTor.Contracts;

public static class UsbProvisioningCapacity
{
    public const long MinimumDataImageBytes = 1L * 1024 * 1024 * 1024;
    public const long MetadataAllowanceBytes = 256L * 1024 * 1024;
    public const long LayoutSafetyBytes = 128L * 1024 * 1024;
    private const long SizeGranularityBytes = 1024L * 1024;

    /// <summary>
    /// Plans the private storage file: half of what is left after DexTor OS itself.
    /// </summary>
    /// <remarks>
    /// The storage file used to take all remaining space, so the drive showed as full in
    /// Explorer and could not carry anything else. Half of it now stays free for ordinary
    /// files. A drive with little room still gets the 1 GiB minimum, taken from the free
    /// half, and a drive that cannot fit even that is refused as before.
    /// </remarks>
    public static bool TryCalculateDataImageSize(
        long diskCapacityBytes,
        long systemImageSizeBytes,
        out long dataImageSizeBytes)
    {
        dataImageSizeBytes = 0;
        if (diskCapacityBytes <= 0 || systemImageSizeBytes <= 0) return false;

        try
        {
            var fixedBytes = checked(systemImageSizeBytes * 2 + MetadataAllowanceBytes + LayoutSafetyBytes);
            var availableForData = checked(diskCapacityBytes - fixedBytes);
            if (availableForData < MinimumDataImageBytes) return false;
            var half = availableForData / 2 / SizeGranularityBytes * SizeGranularityBytes;
            dataImageSizeBytes = Math.Max(MinimumDataImageBytes, half);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
