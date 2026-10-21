namespace DexTor.Provisioner;

public enum ProvisionerOperation
{
    EnableWhpx,
    InspectUsbDevice,
    LockUsbVolumes,
    DismountUsbVolumes,
    InitializeUsb,
    CreateUsbVolume,
    FormatUsbVolume,
    CreateHybridUsb,
    FlushUsbDevice,
    EjectUsbDevice,
}

public sealed record ProvisionerRequest(ProvisionerOperation Operation, string? Target = null);
public sealed record ProvisionerResult(ProvisionerOperation Operation, bool Success, string? Detail = null);
