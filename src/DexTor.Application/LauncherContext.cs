using System.Collections.Generic;

namespace DexTor.Application;

public sealed class LauncherContext
{
    public bool IsUsbConnected { get; init; }
    public bool IsDexTorUsb { get; init; }
    public bool HasSetupInterrupted { get; init; }
    public bool IsWhpxEnabled { get; init; }
    public bool NeedsReboot { get; init; }
    public bool HasRequiredUpdate { get; init; }
    public bool HasOptionalUpdate { get; init; }
    public bool HasUncleanSession { get; init; }
    public bool IsSessionRunning { get; init; }
    public string? UsbRootPath { get; init; }
    public IReadOnlyList<string> UsbRootCandidates { get; init; } = [];
    public string? DextorRootPath { get; init; }
}
