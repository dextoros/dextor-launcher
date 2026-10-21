namespace DexTor.Application;

public sealed class PrimaryActionResolver
{
    public static PrimaryAction Resolve(LauncherContext context)
    {
        if (!context.IsWhpxEnabled)
        {
            return context.NeedsReboot
                ? PrimaryAction.RestartRequired
                : PrimaryAction.PrepareThisPc;
        }

        if (context.IsSessionRunning)
        {
            return PrimaryAction.EndSession;
        }

        if (context.HasUncleanSession)
        {
            return PrimaryAction.CheckAndRecover;
        }

        if (!context.IsUsbConnected)
        {
            return PrimaryAction.ConnectDexTorUsb;
        }

        if (context.HasSetupInterrupted)
        {
            return PrimaryAction.ContinueSetup;
        }

        if (!context.IsDexTorUsb)
        {
            return PrimaryAction.CreateDexTorUsb;
        }

        if (context.HasRequiredUpdate)
        {
            return PrimaryAction.UpdateAndStart;
        }

        if (context.HasOptionalUpdate)
        {
            return PrimaryAction.RepairAndStart;
        }

        return PrimaryAction.StartDexTor;
    }
}
