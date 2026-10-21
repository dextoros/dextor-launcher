using System.Text.Json;

namespace DexTor.Contracts;

public sealed record UsbPhysicalIdentity(
    int DiskNumber,
    string UniqueId,
    string SerialNumber,
    long CapacityBytes,
    string BusType,
    string FriendlyName,
    bool IsBoot,
    bool IsSystem,
    bool IsReadOnly);

public sealed record UsbProvisioningRequest(
    Guid RequestId,
    UsbPhysicalIdentity Device,
    string SystemImagePath,
    string ExpectedSystemImageSha256,
    long DataImageSizeBytes,
    bool ConfirmErase);

public enum UsbProvisioningStatus
{
    Rejected,
    FailedBeforeDiskChange,
    InterruptedAfterDiskChange,
    Complete
}

public enum UsbProvisioningStage
{
    Validating,
    CleaningDisk,
    WritingSystemImage,
    FormattingDataVolume,
    WaitingForDataVolume,
    CopyingWindowsImage,
    CreatingPrivateStorage,
    Verifying,
    Complete,

    /// <summary>
    /// The device dropped off the bus and is being waited for by identity.
    /// </summary>
    /// <remarks>
    /// Reported so the operator sees that DexTor is waiting for their stick rather than
    /// hanging, and so reconnecting it is visibly the thing that resumes the run. Appended
    /// rather than inserted: the stage travels between the two processes as its numeric
    /// value, and renumbering the existing steps would change what a progress file means.
    /// </remarks>
    WaitingForDevice
}

/// <summary>What the elevated provisioner is doing now, for the wizard to show.</summary>
/// <remarks>
/// <see cref="BytesDone"/> and <see cref="BytesTotal"/> are filled in while a stage moves a
/// known amount of data - writing the image, reading it back, copying and hashing it -
/// so a stage that takes minutes shows movement instead of a bar that stands still.
/// Both are zero for stages without a size, and for progress files written before they
/// existed.
/// </remarks>
public sealed record UsbProvisioningProgress(
    Guid RequestId,
    UsbProvisioningStage Stage,
    DateTimeOffset UpdatedUtc,
    long BytesDone = 0,
    long BytesTotal = 0);

/// <summary>
/// What kind of thing went wrong, where the kind changes what the operator should do.
/// </summary>
/// <remarks>
/// Added so the launcher can stop telling someone to check the USB connection or find
/// another drive when the fault is in the downloaded image. Optional and defaulted, so a
/// result written by an older provisioner still deserializes.
/// </remarks>
public enum UsbProvisioningFailure
{
    /// <summary>Not classified; the message is all there is.</summary>
    Unspecified,

    /// <summary>The Live image's boot assets are missing, unreadable or ambiguous.</summary>
    LiveImageBootAssets,

    /// <summary>The chosen device left the bus and did not come back in time.</summary>
    DeviceDisappeared,

    /// <summary>
    /// A different device now answers on the chosen disk number, so nothing was touched.
    /// </summary>
    DeviceReplaced,

    /// <summary>The device is present but Windows cannot reach its media.</summary>
    DeviceNotReady,

    /// <summary>The device refuses writes.</summary>
    DeviceWriteProtected,

    /// <summary>Something else on this computer is holding the device or its volume.</summary>
    DeviceBusy,

    /// <summary>
    /// The device was there and answered, but refused to store data partway through. A
    /// retry after a fresh start often clears it; a refusal at the same place again
    /// points at a faulty stick or one whose capacity is fake. Appended, not inserted:
    /// the value travels between processes as a number.
    /// </summary>
    DeviceFaulty
}

public sealed record UsbProvisioningResult(
    Guid RequestId,
    UsbProvisioningStatus Status,
    string Message,
    string? DataVolumeRoot = null,
    string? SystemImagePath = null,
    string? DataImagePath = null,
    string? VerifiedSystemImageSha256 = null,
    UsbProvisioningFailure Failure = UsbProvisioningFailure.Unspecified,
    string? Diagnostic = null,
    long? FailureOffsetBytes = null)
{
    public bool Success => Status == UsbProvisioningStatus.Complete;
}

public static class UsbProvisioningSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string SerializeRequest(UsbProvisioningRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static UsbProvisioningRequest? DeserializeRequest(string json) =>
        JsonSerializer.Deserialize<UsbProvisioningRequest>(json, JsonOptions);

    public static string SerializeResult(UsbProvisioningResult result) =>
        JsonSerializer.Serialize(result, JsonOptions);

    public static UsbProvisioningResult? DeserializeResult(string json) =>
        JsonSerializer.Deserialize<UsbProvisioningResult>(json, JsonOptions);

    public static string SerializeProgress(UsbProvisioningProgress progress) =>
        JsonSerializer.Serialize(progress, JsonOptions);

    public static UsbProvisioningProgress? DeserializeProgress(string json) =>
        JsonSerializer.Deserialize<UsbProvisioningProgress>(json, JsonOptions);
}
