using System.Text.Json;
using DexTor.Contracts;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Journal ordering, durability and how a previous session is classified.
/// </summary>
/// <remarks>
/// Ordinary temporary files only. Nothing here writes to a real USB volume, and the
/// durability claim under test is "the content reached the device before the name pointed
/// at it", which a temp file exercises exactly as a USB one would.
/// </remarks>
public sealed class SessionJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-journal-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SessionJournalTests()
    {
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "session.json");
    }

    private SessionJournal Journal => new(_path);

    [Fact]
    public void NoJournalMeansNoPreviousSessionRatherThanAGoodOne()
    {
        var previous = Journal.ReadPrevious();

        previous.Disposition.Should().Be(PreviousSessionDisposition.None);
        previous.RequiresCaution.Should().BeFalse();
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{"updatedUtc":"2026-01-01T00:00:00Z"}""")]
    public void UnreadableOrIncompleteRecordsAreUnknownAndCautious(string content)
    {
        File.WriteAllText(_path, content);

        var previous = Journal.ReadPrevious();

        previous.Disposition.Should().Be(PreviousSessionDisposition.Unknown);
        previous.RequiresCaution.Should().BeTrue("\"cannot tell\" must not be read as \"was fine\"");
    }

    [Fact]
    public void ARunningRecordThatWasNeverClosedOutIsInterrupted()
    {
        Journal.Write(new SessionJournalEntry(Guid.NewGuid(), "running"));

        Journal.ReadPrevious().Disposition.Should().Be(PreviousSessionDisposition.Interrupted);
    }

    [Fact]
    public void AHostFinalizedRecordWithoutGuestEvidenceIsCompletedButUnverified()
    {
        Journal.Write(new SessionJournalEntry(
            Guid.NewGuid(),
            "stopped",
            Report: new SessionFinalizationReport(
                WriterTermination: WriterTerminationResult.Confirmed,
                Journal: JournalPersistenceResult.Persisted),
            HostFinalized: true));

        var previous = Journal.ReadPrevious();

        previous.Disposition.Should().Be(PreviousSessionDisposition.CompletedUnverified);
        previous.RequiresCaution.Should().BeTrue(
            "the host finished, but nothing confirmed the guest closed its storage");
    }

    [Fact]
    public void OnlyConfirmedGuestStorageClosureProducesAVerifiedRecord()
    {
        Journal.Write(new SessionJournalEntry(
            Guid.NewGuid(),
            "stopped",
            Report: new SessionFinalizationReport(
                GuestShutdown: GuestShutdownEvidence.ObservedGuestInitiated,
                GuestStorageClose: GuestStorageCloseEvidence.Confirmed,
                WriterTermination: WriterTerminationResult.Confirmed,
                Journal: JournalPersistenceResult.Persisted),
            HostFinalized: true));

        Journal.ReadPrevious().Disposition.Should().Be(PreviousSessionDisposition.CompletedVerified);
    }

    [Fact]
    public void TheCleanFlagIsNeverSetByHostFinalizationAlone()
    {
        Journal.Write(new SessionJournalEntry(
            Guid.NewGuid(),
            "stopped",
            Report: new SessionFinalizationReport(
                GuestShutdown: GuestShutdownEvidence.ObservedGuestInitiated,
                GuestStorageClose: GuestStorageCloseEvidence.UnconfirmedNoAgent,
                WriterTermination: WriterTerminationResult.Confirmed,
                Flush: HostFlushResult.Succeeded,
                DeviceRemoval: DeviceRemovalResult.Removed,
                Journal: JournalPersistenceResult.Persisted),
            HostFinalized: true));

        using var document = JsonDocument.Parse(File.ReadAllText(_path));

        // QEMU exited, the flush worked and Windows removed the device. None of that is
        // evidence about the guest filesystem.
        document.RootElement.GetProperty("clean").GetBoolean().Should().BeFalse();
        document.RootElement.GetProperty("hostFinalized").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void AForcedOutcomeIsRecordedAndNotErasedByLaterSuccesses()
    {
        Journal.Write(new SessionJournalEntry(
            Guid.NewGuid(),
            "stopped-forced",
            Report: new SessionFinalizationReport(
                Escalation: ShutdownEscalation.JobTerminated,
                WriterTermination: WriterTerminationResult.Confirmed,
                Flush: HostFlushResult.Succeeded,
                DeviceRemoval: DeviceRemovalResult.Removed,
                Journal: JournalPersistenceResult.Persisted),
            HostFinalized: true));

        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        document.RootElement.GetProperty("forced").GetBoolean().Should().BeTrue();
        Journal.ReadPrevious().WasForced.Should().BeTrue();
    }

    [Fact]
    public void AWriteReportsFailureInsteadOfPretendingItPersisted()
    {
        // A directory where the file should be: the write cannot succeed.
        Directory.CreateDirectory(_path);
        var journal = Journal;

        journal.Write(new SessionJournalEntry(Guid.NewGuid(), "running")).Should().BeFalse();
        journal.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void AReplacementNeverLeavesATornRecord()
    {
        var journal = Journal;
        journal.Write(new SessionJournalEntry(Guid.NewGuid(), "running"));
        var first = File.ReadAllText(_path);

        journal.Write(new SessionJournalEntry(Guid.NewGuid(), "stopped", HostFinalized: true));

        // Rename-over-temp: the file is always one whole record, never a mixture.
        var second = File.ReadAllText(_path);
        second.Should().NotBe(first);
        var act = () => JsonDocument.Parse(second).Dispose();
        act.Should().NotThrow();
        File.Exists(_path + ".tmp").Should().BeFalse("the temporary file is renamed, not left behind");
    }

    [Fact]
    public void TheBaseSchemaStaysWhereAnOlderReaderExpectsIt()
    {
        Journal.Write(new SessionJournalEntry(Guid.NewGuid(), "stopped", ExitCode: 7, Error: "detail"));

        using var document = JsonDocument.Parse(File.ReadAllText(_path));
        var root = document.RootElement;

        foreach (var field in new[] { "sessionId", "state", "clean", "exitCode", "error", "updatedUtc" })
        {
            root.TryGetProperty(field, out _).Should().BeTrue($"'{field}' is part of the existing schema");
        }

        root.GetProperty("exitCode").GetInt32().Should().Be(7);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Conservative defaults when a finalization report crosses a version boundary.
/// </summary>
public sealed class SessionFinalizationContractTests
{
    [Fact]
    public void APayloadFromAnOlderBuildReadsAsNothingEstablished()
    {
        // No Phase 6 fields at all, as an older sender would produce.
        var report = SessionFinalizationSerializer.Deserialize("{}");

        report.GuestShutdown.Should().Be(GuestShutdownEvidence.None);
        report.GuestStorageClose.Should().Be(GuestStorageCloseEvidence.UnconfirmedNoAgent);
        report.Escalation.Should().Be(ShutdownEscalation.None);
        report.WriterTermination.Should().Be(WriterTerminationResult.Unconfirmed);
        report.Flush.Should().Be(HostFlushResult.NotAttempted);
        report.Journal.Should().Be(JournalPersistenceResult.NotAttempted);
        report.DeviceRemoval.Should().Be(DeviceRemovalResult.NotRequested);
        report.CanRemoveUsb.Should().BeFalse();
        report.GuestStorageVerifiedClean.Should().BeFalse();
    }

    [Fact]
    public void UnreadablePayloadsAlsoReadAsNothingEstablished()
    {
        SessionFinalizationSerializer.Deserialize("{ not json")
            .WriterTermination.Should().Be(WriterTerminationResult.Unconfirmed);
    }

    [Fact]
    public void ARoundTripPreservesEveryIndependentOutcome()
    {
        var original = new SessionFinalizationReport(
            GuestShutdownEvidence.ObservedPanic,
            GuestStorageCloseEvidence.UnconfirmedNoAgent,
            ShutdownEscalation.JobTerminated,
            WriterTerminationResult.Confirmed,
            HostFlushResult.Denied,
            JournalPersistenceResult.Persisted,
            DeviceRemovalResult.Vetoed,
            "flush detail",
            "veto detail",
            null,
            3);

        var round = SessionFinalizationSerializer.Deserialize(SessionFinalizationSerializer.Serialize(original));

        round.Should().Be(original);
    }

    [Fact]
    public void EscalationIsStickyAndNeverLowered()
    {
        var report = new SessionFinalizationReport(Escalation: ShutdownEscalation.JobTerminated);

        report.WithEscalation(ShutdownEscalation.PowerButton).Escalation
            .Should().Be(ShutdownEscalation.JobTerminated);
        report.WithEscalation(ShutdownEscalation.JobTerminated).WasForced.Should().BeTrue();
    }

    [Fact]
    public void RemovalSuccessAloneIsNotACleanGuestClaim()
    {
        var report = new SessionFinalizationReport(
            GuestShutdown: GuestShutdownEvidence.ObservedGuestInitiated,
            WriterTermination: WriterTerminationResult.Confirmed,
            Flush: HostFlushResult.Succeeded,
            Journal: JournalPersistenceResult.Persisted,
            DeviceRemoval: DeviceRemovalResult.Removed);

        report.CanRemoveUsb.Should().BeTrue();
        report.HostFinalizationComplete.Should().BeTrue();
        report.GuestStorageVerifiedClean.Should().BeFalse("no agent reported that storage closed");
    }
}
