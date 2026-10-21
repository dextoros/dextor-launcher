using System.Text.Json;
using DexTor.Contracts;

namespace DexTor.Qemu;

/// <summary>
/// Reads QMP events for the one fact they can actually establish: whether the guest
/// itself began an orderly shutdown.
/// </summary>
/// <remarks>
/// The bar is deliberately narrow. A <c>SHUTDOWN</c> event with <c>guest: true</c> and a
/// reason of <c>guest-shutdown</c> is the guest saying it chose to stop. Nothing else
/// stands in for it:
/// <list type="bullet">
/// <item>a command acknowledgement only says <c>system_powerdown</c> was parsed;</item>
/// <item><c>POWERDOWN</c> says the button was pressed, not that anyone answered it;</item>
/// <item>end of stream and exit code zero say the process ended;</item>
/// <item><c>GUEST_PANICKED</c> and a reset are the opposite of orderly;</item>
/// <item>a reason DexTor does not recognise is not evidence of anything.</item>
/// </list>
/// And even the narrow case establishes only that the guest began shutting down — never
/// that it finished closing its storage, which needs a guest agent this build does not
/// have.
/// </remarks>
public static class QmpShutdownEvidence
{
    public const string ShutdownEvent = "SHUTDOWN";
    public const string PanicEvent = "GUEST_PANICKED";
    public const string ResetEvent = "RESET";
    public const string StopEvent = "STOP";
    public const string PowerdownEvent = "POWERDOWN";
    public const string BlockIoErrorEvent = "BLOCK_IO_ERROR";

    private const string GuestShutdownReason = "guest-shutdown";

    /// <summary>
    /// Classifies one event, or returns <see cref="GuestShutdownEvidence.None"/> when the
    /// event says nothing about how the guest stopped.
    /// </summary>
    public static GuestShutdownEvidence Classify(QmpEvent qmpEvent)
    {
        ArgumentNullException.ThrowIfNull(qmpEvent);

        if (string.Equals(qmpEvent.Event, PanicEvent, StringComparison.Ordinal))
        {
            return GuestShutdownEvidence.ObservedPanic;
        }

        if (!string.Equals(qmpEvent.Event, ShutdownEvent, StringComparison.Ordinal))
        {
            return GuestShutdownEvidence.None;
        }

        // A SHUTDOWN without data is still a host-visible shutdown, but not a guest one.
        if (qmpEvent.Data is not { ValueKind: JsonValueKind.Object } data)
        {
            return GuestShutdownEvidence.ObservedHostInitiated;
        }

        var guest = data.TryGetProperty("guest", out var guestElement)
            && guestElement.ValueKind == JsonValueKind.True;
        var reason = data.TryGetProperty("reason", out var reasonElement)
            && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;

        // Both halves are required. QEMU reports guest:true for a guest-initiated reset
        // too, and an unrecognised reason is not something to read optimistically.
        return guest && string.Equals(reason, GuestShutdownReason, StringComparison.Ordinal)
            ? GuestShutdownEvidence.ObservedGuestInitiated
            : GuestShutdownEvidence.ObservedHostInitiated;
    }

    /// <summary>
    /// Keeps the strongest evidence seen. Orderly guest shutdown outranks everything;
    /// a panic is never overwritten by a later, vaguer event.
    /// </summary>
    public static GuestShutdownEvidence Strongest(GuestShutdownEvidence current, GuestShutdownEvidence observed) =>
        (current, observed) switch
        {
            (GuestShutdownEvidence.ObservedGuestInitiated, _) => current,
            (_, GuestShutdownEvidence.None) => current,
            (GuestShutdownEvidence.ObservedPanic, GuestShutdownEvidence.ObservedHostInitiated) => current,
            _ => observed
        };
}
