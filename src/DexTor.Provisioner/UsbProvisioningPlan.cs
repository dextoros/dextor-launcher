using DexTor.Contracts;

namespace DexTor.Provisioner;

public sealed record PhysicalDiskSnapshot(
    int DiskNumber,
    string UniqueId,
    string SerialNumber,
    long CapacityBytes,
    string BusType,
    string FriendlyName,
    bool IsBoot,
    bool IsSystem,
    bool IsReadOnly,
    bool IsOffline);

public sealed record UsbProvisioningPlan(
    int DiskNumber,
    char DataDriveLetter,
    string PrepareDiskScript,
    string FormatDataPartitionScript,
    string FinalizeDataPartitionScript,
    string ClearReadOnlyScript);

public static class UsbProvisioningPlanner
{
    public const long MinimumDataImageBytes = UsbProvisioningCapacity.MinimumDataImageBytes;
    public static UsbProvisioningPlan Build(
        UsbProvisioningRequest request,
        PhysicalDiskSnapshot actualDisk,
        char dataDriveLetter,
        int dataPartitionEntryIndex = 5)
    {
        ValidateRequest(request, actualDisk);
        var dataLetter = NormalizeDriveLetter(dataDriveLetter);
        if (dataPartitionEntryIndex < 1)
        {
            throw new InvalidOperationException("A valid GPT data partition index is required.");
        }

        var prepareScript = string.Join(Environment.NewLine,
        [
            $"select disk {actualDisk.DiskNumber}",
            "attributes disk clear readonly",
            "clean",
            // A removable disk left without any partition table is shown by Windows as a
            // single volume over the whole disk, under its old letter. As the image was
            // written, that volume was recognized as the ISO9660 "DexTor OS" and opened in
            // Explorer. An empty GPT means there is no such volume to recognize; the raw
            // write then replaces the table with the image's own.
            "convert gpt",
            "exit",
            string.Empty
        ]);
        // The rescan comes first and the disk is selected after it, so the selection is
        // made against the partition table Windows has just re-read, not the one it held
        // before the raw image was written.
        var formatScript = string.Join(Environment.NewLine,
        [
            "rescan",
            $"select disk {actualDisk.DiskNumber}",
            $"select partition {dataPartitionEntryIndex}",
            // Formatted while it still carries "no default drive letter": nothing mounts
            // it under a letter, so no Explorer or AutoPlay window opens mid-creation.
            "format quick fs=ntfs label=DEXTOR",
            "exit",
            string.Empty
        ]);
        var finalizeScript = string.Join(Environment.NewLine,
        [
            $"select disk {actualDisk.DiskNumber}",
            $"select partition {dataPartitionEntryIndex}",
            "gpt attributes=0x0000000000000000",
            $"assign letter={dataLetter}",
            "exit",
            string.Empty
        ]);

        // Used when the disk's volumes are locked: the image then replaces the old layout
        // directly, and nothing is cleaned.
        var clearReadOnlyScript = string.Join(Environment.NewLine,
        [
            $"select disk {actualDisk.DiskNumber}",
            "attributes disk clear readonly",
            "exit",
            string.Empty
        ]);

        return new UsbProvisioningPlan(actualDisk.DiskNumber, dataLetter, prepareScript, formatScript, finalizeScript, clearReadOnlyScript);
    }

    public static void ValidateRequest(UsbProvisioningRequest request, PhysicalDiskSnapshot actualDisk)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actualDisk);
        if (!request.ConfirmErase)
        {
            throw new InvalidOperationException("Explicit erase confirmation is required.");
        }

        if (request.RequestId == Guid.Empty)
        {
            throw new InvalidOperationException("A unique request ID is required.");
        }

        var expected = request.Device;
        if (expected.DiskNumber != actualDisk.DiskNumber
            || expected.CapacityBytes != actualDisk.CapacityBytes
            || !string.Equals(expected.UniqueId.Trim(), actualDisk.UniqueId.Trim(), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.SerialNumber.Trim(), actualDisk.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The selected USB identity changed before provisioning.");
        }

        if (string.IsNullOrWhiteSpace(expected.UniqueId) || string.IsNullOrWhiteSpace(actualDisk.UniqueId))
        {
            throw new InvalidOperationException("The USB does not expose a stable unique ID.");
        }

        if (!string.Equals(expected.BusType, "USB", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actualDisk.BusType, "USB", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only a physical USB disk can be provisioned.");
        }

        if (expected.IsBoot || expected.IsSystem || actualDisk.IsBoot || actualDisk.IsSystem)
        {
            throw new InvalidOperationException("Windows boot and system disks are protected.");
        }

        if (expected.IsReadOnly || actualDisk.IsReadOnly)
        {
            throw new InvalidOperationException("The selected USB is read-only.");
        }

        if (request.DataImageSizeBytes < MinimumDataImageBytes)
        {
            throw new InvalidOperationException("The data image size is too small.");
        }

        if (request.ExpectedSystemImageSha256.Length != 64
            || !request.ExpectedSystemImageSha256.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException("A valid system image SHA-256 is required.");
        }
    }

    private static char NormalizeDriveLetter(char driveLetter)
    {
        var normalized = char.ToUpperInvariant(driveLetter);
        if (normalized is < 'D' or > 'Z')
        {
            throw new InvalidOperationException("Provisioning drive letters must be between D and Z.");
        }

        return normalized;
    }
}
