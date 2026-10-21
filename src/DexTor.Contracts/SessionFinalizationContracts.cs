using System.Text.Json;
using System.Text.Json.Serialization;

namespace DexTor.Contracts;

/// <summary>
/// What was observed about the guest deciding to shut down.
/// </summary>
/// <remarks>
/// Zero is the conservative member on every enum here, so a payload written by an older
/// build — or any field that fails to deserialize — reads as "nothing was established"
/// rather than as success.
/// </remarks>
public enum GuestShutdownEvidence
{
    /// <summary>Nothing was observed.</summary>
    None = 0,

    /// <summary>
    /// QMP reported <c>SHUTDOWN</c> with <c>guest: true</c> and a guest-shutdown reason:
    /// the guest itself began an orderly shutdown.
    /// </summary>
    ObservedGuestInitiated,

    /// <summary>
    /// QMP reported <c>SHUTDOWN</c>, but not one the guest initiated — a host request, a
    /// reset, or a reason DexTor does not recognise.
    /// </summary>
    ObservedHostInitiated,

    /// <summary>The guest panicked. Not an orderly shutdown.</summary>
    ObservedPanic,

    /// <summary>QEMU exited without any shutdown event to explain it.</summary>
    ProcessExitOnly
}

/// <summary>
/// Whether the guest confirmed it closed and unmounted its private storage.
/// </summary>
public enum GuestStorageCloseEvidence
{
    /// <summary>
    /// No guest agent exists, so no such report can arrive. This is the production value
    /// for this milestone. It means <em>not verified</em>, never <em>known damaged</em>.
    /// </summary>
    UnconfirmedNoAgent = 0,

    /// <summary>A guest agent reported storage closed. Not reachable in this build.</summary>
    Confirmed,

    /// <summary>A guest agent reported that it could not close storage.</summary>
    ReportedFailure
}

/// <summary>
/// How far shutdown escalation had to go. Sticky: once a stage is reached, nothing later
/// lowers it.
/// </summary>
public enum ShutdownEscalation
{
    /// <summary>Nothing was asked; the writer was already gone.</summary>
    None = 0,

    /// <summary>QMP <c>system_powerdown</c> was enough.</summary>
    PowerButton,

    /// <summary>QMP <c>quit</c> was needed. The guest did not stop on request.</summary>
    Quit,

    /// <summary>The session job had to be terminated. The guest was forced to stop.</summary>
    JobTerminated
}

/// <summary>Whether every process of this session is provably gone.</summary>
public enum WriterTerminationResult
{
    /// <summary>
    /// Could not be confirmed. A writer may still hold <c>data.img</c>, so the device
    /// stays claimed and nothing downstream may run.
    /// </summary>
    Unconfirmed = 0,

    /// <summary>
    /// The original process handle is signalled and the session job reports no members.
    /// </summary>
    Confirmed
}

/// <summary>Result of flushing the host volume that holds <c>data.img</c>.</summary>
public enum HostFlushResult
{
    /// <summary>Not reached, because something earlier was unresolved.</summary>
    NotAttempted = 0,

    /// <summary>The volume flush returned success.</summary>
    Succeeded,

    /// <summary>The volume could not be opened for writing; this needs Administrator.</summary>
    Denied,

    /// <summary>The volume opened but the flush failed.</summary>
    Failed,

    /// <summary>No flushable volume could be resolved for the image.</summary>
    Unavailable,

    /// <summary>
    /// A flush was started and has not returned. The operation is still tracked; this is
    /// not a failure and not a success.
    /// </summary>
    InProgress
}

/// <summary>Whether the session outcome reached durable storage.</summary>
public enum JournalPersistenceResult
{
    NotAttempted = 0,
    Persisted,
    Failed
}

/// <summary>Result of asking Windows to remove the device.</summary>
public enum DeviceRemovalResult
{
    /// <summary>The caller only asked to stop the session.</summary>
    NotRequested = 0,

    /// <summary>Windows reported the device removed. Only this permits "safe to remove".</summary>
    Removed,

    /// <summary>Windows or a driver vetoed removal. The veto reason is preserved.</summary>
    Vetoed,

    /// <summary>The request was refused for lack of privilege.</summary>
    Denied,

    /// <summary>The request reached Windows and failed for another reason.</summary>
    Failed,

    /// <summary>
    /// The device could not be resolved to one verified removable target, or the mapping
    /// was ambiguous. Nothing was asked of Windows.
    /// </summary>
    TargetUnresolved,

    /// <summary>
    /// Removal was not attempted because session writers or host operations were not
    /// settled. Never a failure of the device.
    /// </summary>
    Skipped
}

/// <summary>
/// Everything one session finalization established, as independent facts.
/// </summary>
/// <remarks>
/// The point of keeping these apart is that none of them implies another. QEMU exiting
/// says nothing about the guest filesystem; a successful flush says bytes moved, not that
/// they were consistent; Windows accepting a removal says nothing about either. A caller
/// that wants a single green light has to say which facts it is willing to accept, and
/// <see cref="CanRemoveUsb"/> is the only one this type will assert on its own.
/// </remarks>
public sealed record SessionFinalizationReport(
    GuestShutdownEvidence GuestShutdown = GuestShutdownEvidence.None,
    GuestStorageCloseEvidence GuestStorageClose = GuestStorageCloseEvidence.UnconfirmedNoAgent,
    ShutdownEscalation Escalation = ShutdownEscalation.None,
    WriterTerminationResult WriterTermination = WriterTerminationResult.Unconfirmed,
    HostFlushResult Flush = HostFlushResult.NotAttempted,
    JournalPersistenceResult Journal = JournalPersistenceResult.NotAttempted,
    DeviceRemovalResult DeviceRemoval = DeviceRemovalResult.NotRequested,
    string? FlushDetail = null,
    string? DeviceRemovalDetail = null,
    string? WriterTerminationDetail = null,
    int? ExitCode = null)
{
    /// <summary>
    /// True when the host side finished: writers provably gone and the outcome durably
    /// recorded. Says nothing about the guest's own filesystem.
    /// </summary>
    public bool HostFinalizationComplete =>
        WriterTermination == WriterTerminationResult.Confirmed
        && Journal == JournalPersistenceResult.Persisted;

    /// <summary>
    /// The comprehensive claim: an orderly guest shutdown <em>and</em> a guest report
    /// that storage was closed, on top of a complete host finalization.
    /// </summary>
    /// <remarks>
    /// Without a guest agent this is always false, and that is the intended production
    /// answer — not a defect and not a claim of damage. It must never be set because QEMU
    /// exited, because a flush succeeded, or because Windows accepted a removal.
    /// </remarks>
    public bool GuestStorageVerifiedClean =>
        HostFinalizationComplete
        && GuestShutdown == GuestShutdownEvidence.ObservedGuestInitiated
        && GuestStorageClose == GuestStorageCloseEvidence.Confirmed;

    /// <summary>True only when Windows actually removed the verified device.</summary>
    public bool CanRemoveUsb => DeviceRemoval == DeviceRemovalResult.Removed;

    /// <summary>True when the guest was stopped rather than stopping itself.</summary>
    public bool WasForced => Escalation >= ShutdownEscalation.Quit;

    /// <summary>
    /// Merges a later stage into this report without ever lowering a sticky field.
    /// </summary>
    public SessionFinalizationReport WithEscalation(ShutdownEscalation escalation) =>
        escalation > Escalation ? this with { Escalation = escalation } : this;
}

public static class SessionFinalizationSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(SessionFinalizationReport report) =>
        JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>
    /// Reads a report, treating anything absent or unreadable as conservative.
    /// </summary>
    /// <remarks>
    /// A payload from an older build has none of these fields, and an unknown enum name
    /// from a newer one cannot be honoured. Both must land on "not established" rather
    /// than on the first enum member happening to mean success — which is why zero is the
    /// conservative value on every enum here.
    /// </remarks>
    public static SessionFinalizationReport Deserialize(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SessionFinalizationReport>(json, JsonOptions)
                ?? new SessionFinalizationReport();
        }
        catch (JsonException)
        {
            return new SessionFinalizationReport();
        }
    }
}
