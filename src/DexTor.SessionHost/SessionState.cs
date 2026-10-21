namespace DexTor.SessionHost;

public enum SessionState
{
    Idle,
    StartingQemu,
    Running,
    Stopping,
    StorageLocking,
    SafeToEject
}
