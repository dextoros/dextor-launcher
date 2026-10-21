using System.Runtime.InteropServices;
using System.Text.Json;
using DexTor.Contracts;

namespace DexTor.SessionHost;

/// <summary>How a previous session ended, as the journal records it.</summary>
public enum PreviousSessionDisposition
{
    /// <summary>No journal, unreadable, or a schema this build does not understand.</summary>
    Unknown = 0,

    /// <summary>No previous session is recorded for this device.</summary>
    None,

    /// <summary>
    /// A session was started and never reached an end record. The writer was interrupted.
    /// </summary>
    Interrupted,

    /// <summary>
    /// The host side finished — writers gone, outcome recorded — but the guest's own
    /// storage closure was never verified.
    /// </summary>
    CompletedUnverified,

    /// <summary>Host finalization completed and a guest agent confirmed storage closed.</summary>
    CompletedVerified
}

/// <summary>What the journal says about the session before this one.</summary>
public sealed record PreviousSessionRecord(
    PreviousSessionDisposition Disposition,
    Guid? SessionId = null,
    string? State = null,
    string? Error = null,
    bool WasForced = false)
{
    /// <summary>
    /// True when the next session must not assume the image was left consistent.
    /// </summary>
    /// <remarks>
    /// Everything except a verified completion earns this, including
    /// <see cref="PreviousSessionDisposition.Unknown"/>: "cannot tell" and "was fine"
    /// must not share an outcome.
    /// </remarks>
    public bool RequiresCaution => Disposition is not PreviousSessionDisposition.CompletedVerified
        and not PreviousSessionDisposition.None;
}

/// <summary>
/// The session journal: one current-state file, written atomically and durably.
/// </summary>
/// <remarks>
/// Same location and same base schema as before — <c>%LOCALAPPDATA%\DexTor\session.json</c>
/// by default — with the Phase 6 outcome fields added alongside the existing ones, so an
/// older reader still finds <c>state</c>, <c>clean</c>, <c>exitCode</c> and <c>error</c>
/// where it expects them. No new marker is written to the USB.
/// <para>
/// <c>clean</c> keeps its name but now means the comprehensive claim: host finalization
/// complete <em>and</em> guest storage closure verified. Without a guest agent it is
/// always false, which is the honest answer rather than a regression.
/// </para>
/// </remarks>
public sealed partial class SessionJournal(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Path => path;

    /// <summary>
    /// Reads the previous record before anything replaces it.
    /// </summary>
    /// <remarks>
    /// Read errors and unrecognised content both produce
    /// <see cref="PreviousSessionDisposition.Unknown"/>. Recovery consults this and never
    /// the ownership mutex: a mutex that is free, or one taken over from an abandoned
    /// owner, says a process died — it says nothing about that process's writes.
    /// </remarks>
    public PreviousSessionRecord ReadPrevious()
    {
        string text;
        try
        {
            if (!File.Exists(path))
            {
                return new PreviousSessionRecord(PreviousSessionDisposition.None);
            }

            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new PreviousSessionRecord(PreviousSessionDisposition.Unknown, Error: exception.Message);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var state = root.TryGetProperty("state", out var stateElement) ? stateElement.GetString() : null;
            var error = root.TryGetProperty("error", out var errorElement) ? errorElement.GetString() : null;
            var sessionId = root.TryGetProperty("sessionId", out var idElement)
                && idElement.ValueKind == JsonValueKind.String
                && Guid.TryParse(idElement.GetString(), out var parsed)
                    ? parsed
                    : (Guid?)null;
            var forced = root.TryGetProperty("forced", out var forcedElement)
                && forcedElement.ValueKind == JsonValueKind.True;

            return new PreviousSessionRecord(Classify(root, state), sessionId, state, error, forced);
        }
        catch (JsonException exception)
        {
            return new PreviousSessionRecord(PreviousSessionDisposition.Unknown, Error: exception.Message);
        }
    }

    private static PreviousSessionDisposition Classify(JsonElement root, string? state)
    {
        if (state is null)
        {
            return PreviousSessionDisposition.Unknown;
        }

        // Only a record that says finalization ran can be anything but interrupted.
        var finalized = root.TryGetProperty("hostFinalized", out var finalizedElement)
            && finalizedElement.ValueKind == JsonValueKind.True;
        var verified = root.TryGetProperty("clean", out var cleanElement)
            && cleanElement.ValueKind == JsonValueKind.True;

        if (verified)
        {
            return PreviousSessionDisposition.CompletedVerified;
        }

        if (finalized)
        {
            return PreviousSessionDisposition.CompletedUnverified;
        }

        return state switch
        {
            "idle" => PreviousSessionDisposition.None,

            // Written before the writer existed, or while it ran: nothing closed it out.
            "running" or "starting" or "storage-locking" or "stopping" => PreviousSessionDisposition.Interrupted,

            // Older builds wrote these end states without hostFinalized.
            "stopped" => PreviousSessionDisposition.CompletedUnverified,
            _ => PreviousSessionDisposition.Interrupted
        };
    }

    /// <summary>
    /// Writes a record and reports whether it reached the disk.
    /// </summary>
    /// <param name="durable">
    /// When true the bytes are forced to the device before the rename, and the caller may
    /// treat a true return as persisted. This is what the dirty-before-writer record and
    /// the final outcome both need; progress notes do not.
    /// </param>
    /// <remarks>
    /// Temp file, flush, replace. Two separate guarantees are needed and neither implies
    /// the other:
    /// <list type="number">
    /// <item>the record's <em>content</em> reaches the device — <c>FILE_FLAG_WRITE_THROUGH</c>
    /// plus <c>FlushFileBuffers</c> on the temporary file;</item>
    /// <item>the <em>rename</em> that makes that content the journal is committed —
    /// <c>MoveFileEx</c> with <c>MOVEFILE_WRITE_THROUGH</c>.</item>
    /// </list>
    /// <c>File.Move</c> issues <c>MoveFileEx</c> without <c>MOVEFILE_WRITE_THROUGH</c>, so
    /// an earlier <c>Flush(true)</c> on the temporary file said nothing about whether the
    /// directory entry had been committed. That is why the replace is issued directly here.
    /// <para>
    /// What this does and does not claim: the replace is atomic, so a crash leaves either
    /// the whole old record or the whole new one and never a torn one, and with the flag
    /// the call does not return until the operation has been carried through to the disk.
    /// Microsoft documents <c>MOVEFILE_WRITE_THROUGH</c>'s flush guarantee most explicitly
    /// for a move performed as copy-and-delete; for a same-volume rename NTFS journals the
    /// metadata change. Neither this nor anything else in the process can speak for what
    /// a device does with its own write cache after Windows has been told to commit.
    /// </para>
    /// </remarks>
    public bool Write(SessionJournalEntry entry, bool durable = true)
    {
        ArgumentNullException.ThrowIfNull(entry);
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Invalid journal path.");
            Directory.CreateDirectory(directory);

            var temporary = path + ".tmp";
            var payload = JsonSerializer.Serialize(entry.ToPayload(), JsonOptions);
            using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                durable ? FileOptions.WriteThrough : FileOptions.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(payload);
                writer.Flush();
                if (durable)
                {
                    stream.Flush(flushToDisk: true);
                }
            }

            Replace(temporary, path, durable);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidOperationException)
        {
            LastError = exception.Message;
            return false;
        }
    }

    /// <summary>Why the last write failed, when it did.</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// Replaces the journal with the temporary file, asking Windows to commit the rename
    /// when the record has to be durable.
    /// </summary>
    private static void Replace(string temporary, string destination, bool durable)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(temporary, destination, true);
            return;
        }

        var flags = MoveFileReplaceExisting | (durable ? MoveFileWriteThrough : 0);
        if (!MoveFileExW(temporary, destination, flags))
        {
            throw new IOException(
                "The session journal could not be replaced.",
                Marshal.GetLastPInvokeError());
        }
    }

    private const uint MoveFileReplaceExisting = 0x00000001;
    private const uint MoveFileWriteThrough = 0x00000008;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileExW(string existingFileName, string newFileName, uint flags);
}

/// <summary>One journal record. The pre-Phase-6 fields keep their names and meanings.</summary>
public sealed record SessionJournalEntry(
    Guid? SessionId,
    string State,
    int? ExitCode = null,
    string? Error = null,
    SessionFinalizationReport? Report = null,
    bool HostFinalized = false)
{
    internal object ToPayload()
    {
        var report = Report;
        return new
        {
            sessionId = SessionId,
            state = State,

            // The comprehensive claim, and only that. Host finalization alone never sets
            // it, so without a guest agent it stays false.
            clean = report?.GuestStorageVerifiedClean ?? false,
            hostFinalized = HostFinalized,
            forced = report?.WasForced ?? false,
            exitCode = ExitCode ?? report?.ExitCode,
            error = Error,
            guestShutdown = report?.GuestShutdown.ToString(),
            guestStorageClose = report?.GuestStorageClose.ToString(),
            escalation = report?.Escalation.ToString(),
            writerTermination = report?.WriterTermination.ToString(),
            flush = report?.Flush.ToString(),
            deviceRemoval = report?.DeviceRemoval.ToString(),
            updatedUtc = DateTimeOffset.UtcNow
        };
    }
}
