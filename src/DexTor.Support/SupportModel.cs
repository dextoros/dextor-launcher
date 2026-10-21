using System.Text.Json;

namespace DexTor.Support;

public enum SupportCode
{
    UnsupportedWindowsVersion = 1001,
    WhpxNotEnabled = 2001,
    VirtualizationDisabled = 2002,
    UsbNotEligible = 3001,
    UsbChangedDuringSetup = 3002,
    UsbReadOnly = 3003,
    UsbTooSmall = 3004,
    SetupInterrupted = 3005,
    UsbIoError = 3006,
    InvalidImage = 4001,
    ImageVerificationFailed = 4002,
    QemuFailedStart = 5001,
    GuestAgentTimeout = 5002,
    StorageProvisionFailed = 6001,
    StorageNeedsRecovery = 6002,
    UpdateFailed = 7001,
}

public sealed class SupportReport
{
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? DeviceInstanceId { get; init; }
    public SupportCode Code { get; init; }
    public string? Message { get; init; }
    public string? DeviceFingerprint { get; init; }
    public string ToJson() => JsonSerializer.Serialize(this);
}
