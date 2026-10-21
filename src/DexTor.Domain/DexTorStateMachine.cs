using System.Collections.Generic;

namespace DexTor.Domain;

public sealed class DexTorStateMachine
{
    private static readonly Dictionary<(BetaState, BetaEvent), BetaState> _transitions = new Dictionary<(BetaState, BetaEvent), BetaState>
    {
        [(BetaState.Starting, BetaEvent.Start)] = BetaState.SelfCheck,
        [(BetaState.SelfCheck, BetaEvent.HostCheckPassed)] = BetaState.HostCheck,
        [(BetaState.HostCheck, BetaEvent.WhpxMissing)] = BetaState.NeedsWhpx,
        [(BetaState.NeedsWhpx, BetaEvent.WhpxOk)] = BetaState.NeedsReboot,
        [(BetaState.NeedsReboot, BetaEvent.RebootDone)] = BetaState.HostCheck,
        [(BetaState.HostCheck, BetaEvent.Continue)] = BetaState.ImagePreparation,
        [(BetaState.ImagePreparation, BetaEvent.DownloadPrepared)] = BetaState.WaitingForUsb,
        [(BetaState.HostCheck, BetaEvent.UsbPlugged)] = BetaState.WaitingForUsb,
        [(BetaState.WaitingForUsb, BetaEvent.UsbClassifiedDexTor)] = BetaState.DexTorUsbDetected,
        [(BetaState.WaitingForUsb, BetaEvent.UsbClassifiedNormal)] = BetaState.NormalUsbDetected,
        [(BetaState.NormalUsbDetected, BetaEvent.UserSelectedUsb)] = BetaState.UsbSelected,
        [(BetaState.UsbSelected, BetaEvent.DownloadPrepared)] = BetaState.ReleasePreparing,
        [(BetaState.ReleasePreparing, BetaEvent.EraseApproved)] = BetaState.EraseConfirmation,
        [(BetaState.EraseConfirmation, BetaEvent.FormatDone)] = BetaState.FormattingUsb,
        [(BetaState.FormattingUsb, BetaEvent.SystemImageWritten)] = BetaState.WritingSystem,
        [(BetaState.WritingSystem, BetaEvent.DataImageCreated)] = BetaState.CreatingDataImage,
        [(BetaState.CreatingDataImage, BetaEvent.UsbVerified)] = BetaState.VerifyingUsb,
        [(BetaState.VerifyingUsb, BetaEvent.GuestProvisioned)] = BetaState.ProvisioningGuest,
        [(BetaState.ProvisioningGuest, BetaEvent.VerificationDone)] = BetaState.Ready,
        [(BetaState.Launching, BetaEvent.StorageLocked)] = BetaState.StorageLocked,
        [(BetaState.StorageLocked, BetaEvent.SessionRunning)] = BetaState.Running,
        [(BetaState.Running, BetaEvent.SessionStopRequested)] = BetaState.Stopping,
        [(BetaState.Stopping, BetaEvent.SessionStopped)] = BetaState.StorageClosed,
        [(BetaState.StorageClosed, BetaEvent.SafeRemovalComplete)] = BetaState.SafeToRemove,
        [(BetaState.Running, BetaEvent.UsbDisconnected)] = BetaState.UsbRemoved,
        [(BetaState.UsbRemoved, BetaEvent.RecoveryCheckRequired)] = BetaState.RecoveryRequired,
        [(BetaState.DexTorUsbDetected, BetaEvent.VerificationDone)] = BetaState.UpdateCheck,
        [(BetaState.UpdateCheck, BetaEvent.UpdateAvailable)] = BetaState.Updating,
        [(BetaState.UpdateCheck, BetaEvent.UpdateRequired)] = BetaState.Updating,
        [(BetaState.UpdateCheck, BetaEvent.UpdateComplete)] = BetaState.Ready,
        [(BetaState.Updating, BetaEvent.UpdateComplete)] = BetaState.Ready,
        [(BetaState.Ready, BetaEvent.LaunchStarted)] = BetaState.Launching,
        [(BetaState.Ready, BetaEvent.LaunchCompleted)] = BetaState.StorageLocked,
        [(BetaState.StorageLocked, BetaEvent.GuestProvisionedFailed)] = BetaState.SetupInterrupted,
        [(BetaState.StorageClosed, BetaEvent.GuestProvisionedFailed)] = BetaState.RecoveryRequired,
        [(BetaState.VerifyingUsb, BetaEvent.GuestProvisionedFailed)] = BetaState.SetupInterrupted,
        [(BetaState.ProvisioningGuest, BetaEvent.GuestProvisionedFailed)] = BetaState.SetupInterrupted
    };

    public DexTorStateMachine(BetaState initialState = BetaState.Starting)
    {
        State = initialState;
    }

    public BetaState State { get; private set; }

    public BetaState Apply(BetaEvent @event)
    {
        if (State == BetaState.FatalError)
        {
            throw new BetaStateTransitionException(State, @event);
        }

        if (!_transitions.TryGetValue((State, @event), out var next))
        {
            if (@event == BetaEvent.Fatal)
            {
                State = BetaState.FatalError;
                return State;
            }

            throw new BetaStateTransitionException(State, @event);
        }

        State = next;
        return State;
    }

    public bool CanApply(BetaEvent @event) => _transitions.ContainsKey((State, @event)) || @event == BetaEvent.Fatal;

    public static IReadOnlyCollection<BetaState> StatesWithUserDecision =>
        new[]
        {
            BetaState.NeedsWhpx,
            BetaState.ImagePreparation,
            BetaState.NormalUsbDetected,
            BetaState.EraseConfirmation,
            BetaState.RecoveryRequired
        };
}
