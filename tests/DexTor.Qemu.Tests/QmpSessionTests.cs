using System.Text.Json;
using DexTor.Contracts;
using FluentAssertions;
using Xunit;

namespace DexTor.Qemu.Tests;

/// <summary>
/// The single QMP reader: command correlation, interleaving, end of stream, and what the
/// events are allowed to prove.
/// </summary>
public sealed class QmpSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>A reader the test feeds line by line, so ordering is exact.</summary>
    private sealed class ScriptedReader : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string?> _lines = [];

        public void Emit(string line) => _lines.Add(line);

        public void EndOfStream() => _lines.Add(null);

        public override string? ReadLine() => _lines.IsAddingCompleted && _lines.Count == 0 ? null : _lines.Take();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Stands in for a pipe closing: the reader stops blocking and reports end
                // of stream, which is what releases the session's pump.
                _lines.Add(null);
                _lines.CompleteAdding();
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task ARepliesIsMatchedToItsOwnCommandEvenWhenAnEventArrivesBetween()
    {
        var reader = new ScriptedReader();
        var writer = new StringWriter();
        await using var session = new QmpSession(reader, writer);
        var events = new List<QmpEvent>();
        session.EventReceived += events.Add;
        session.Start();

        var pending = session.ExecuteAsync("query-status", Timeout);

        // An asynchronous event lands between the command and its reply. A reader that
        // assumed the next line was the reply would consume the event as one.
        reader.Emit("""{"event":"POWERDOWN","data":{}}""");
        reader.Emit("""{"return":{"status":"running"},"id":"dextor-1"}""");

        var response = await pending;
        response.Succeeded.Should().BeTrue();
        response.Status.Should().Be("running");
        events.Should().ContainSingle().Which.Event.Should().Be("POWERDOWN");
    }

    [Fact]
    public async Task RepliesOutOfOrderStillReachTheRightCommand()
    {
        var reader = new ScriptedReader();
        await using var session = new QmpSession(reader, new StringWriter());
        session.Start();

        var first = session.ExecuteAsync("query-status", Timeout);
        var second = session.ExecuteAsync("query-version", Timeout);

        // Answered in the opposite order. Correlation is by id, not by arrival.
        reader.Emit("""{"return":{},"id":"dextor-2"}""");
        reader.Emit("""{"return":{"status":"running"},"id":"dextor-1"}""");

        (await second).Succeeded.Should().BeTrue();
        (await first).Status.Should().Be("running");
    }

    [Fact]
    public async Task AFinalEventBufferedJustBeforeEndOfStreamIsStillDelivered()
    {
        var reader = new ScriptedReader();
        await using var session = new QmpSession(reader, new StringWriter());
        var events = new List<QmpEvent>();
        session.EventReceived += events.Add;
        session.Start();

        // QEMU writes SHUTDOWN and exits. The bytes are already in the pipe; the reader
        // must drain them rather than stop at the first sign of the process going away.
        reader.Emit("""{"event":"SHUTDOWN","data":{"guest":true,"reason":"guest-shutdown"}}""");
        reader.EndOfStream();

        await session.Completion;
        events.Should().ContainSingle();
        QmpShutdownEvidence.Classify(events[0]).Should().Be(GuestShutdownEvidence.ObservedGuestInitiated);
    }

    [Fact]
    public async Task EndOfStreamFailsPendingCommandsInsteadOfHangingOnThem()
    {
        var reader = new ScriptedReader();
        await using var session = new QmpSession(reader, new StringWriter());
        session.Start();

        var pending = session.ExecuteAsync("quit", Timeout);
        reader.EndOfStream();

        var response = await pending;
        response.Succeeded.Should().BeFalse();
        response.Error.Should().Contain("closed");
    }

    [Fact]
    public async Task AnUnansweredCommandTimesOutWithoutClaimingItDidNothing()
    {
        var reader = new ScriptedReader();
        await using var session = new QmpSession(reader, new StringWriter());
        session.Start();

        var response = await session.ExecuteAsync("system_powerdown", TimeSpan.FromMilliseconds(150));

        // The command may well have been acted on; all that is known is that no reply
        // arrived, and the wording says exactly that.
        response.Succeeded.Should().BeFalse();
        response.Error.Should().Contain("did not answer");
    }

    [Fact]
    public async Task CommandsCarryAnIdSoQemuCanEchoItBack()
    {
        var reader = new ScriptedReader();
        var writer = new StringWriter();
        await using var session = new QmpSession(reader, writer);
        session.Start();

        var pending = session.ExecuteAsync("qmp_capabilities", Timeout);
        reader.Emit("""{"return":{},"id":"dextor-1"}""");
        await pending;

        using var sent = JsonDocument.Parse(writer.ToString().Trim());
        sent.RootElement.GetProperty("execute").GetString().Should().Be("qmp_capabilities");
        sent.RootElement.GetProperty("id").GetString().Should().Be("dextor-1");
    }

    [Fact]
    public async Task NonJsonNoiseDoesNotStopTheReader()
    {
        var reader = new ScriptedReader();
        await using var session = new QmpSession(reader, new StringWriter());
        session.Start();

        var pending = session.ExecuteAsync("query-status", Timeout);
        reader.Emit("qemu: warning: something on stdout");
        reader.Emit("""{"return":{"status":"running"},"id":"dextor-1"}""");

        (await pending).Status.Should().Be("running");
    }
}

/// <summary>
/// What a QMP event is allowed to establish about how the guest stopped.
/// </summary>
public sealed class QmpShutdownEvidenceTests
{
    private static QmpEvent Event(string name, string? json = null) =>
        new(name, json is null ? null : JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void OnlyAGuestInitiatedGuestShutdownCounts()
    {
        var observed = QmpShutdownEvidence.Classify(
            Event("SHUTDOWN", """{"guest":true,"reason":"guest-shutdown"}"""));

        observed.Should().Be(GuestShutdownEvidence.ObservedGuestInitiated);
    }

    [Theory]
    // The host asked, so the guest did not choose to stop.
    [InlineData("""{"guest":false,"reason":"host-qmp-quit"}""")]
    // guest:true alone is not enough - QEMU reports it for a guest reset too.
    [InlineData("""{"guest":true,"reason":"guest-reset"}""")]
    // A reason this build does not know is not something to read optimistically.
    [InlineData("""{"guest":true,"reason":"something-new"}""")]
    [InlineData("""{"guest":true}""")]
    [InlineData("""{"reason":"guest-shutdown"}""")]
    public void EverythingElseIsOnlyAHostVisibleShutdown(string data)
    {
        QmpShutdownEvidence.Classify(Event("SHUTDOWN", data))
            .Should().Be(GuestShutdownEvidence.ObservedHostInitiated);
    }

    [Fact]
    public void APanicIsNeverAnOrderlyShutdown()
    {
        QmpShutdownEvidence.Classify(Event("GUEST_PANICKED", """{"action":"pause"}"""))
            .Should().Be(GuestShutdownEvidence.ObservedPanic);
    }

    [Theory]
    [InlineData("POWERDOWN")]
    [InlineData("RESET")]
    [InlineData("STOP")]
    [InlineData("BLOCK_IO_ERROR")]
    public void OtherEventsSayNothingAboutHowTheGuestStopped(string name)
    {
        // POWERDOWN in particular means the button was pressed, not that anyone answered.
        QmpShutdownEvidence.Classify(Event(name, "{}")).Should().Be(GuestShutdownEvidence.None);
    }

    [Fact]
    public void APanicIsNotDowngradedByALaterVaguerEvent()
    {
        var after = QmpShutdownEvidence.Strongest(
            GuestShutdownEvidence.ObservedPanic,
            GuestShutdownEvidence.ObservedHostInitiated);

        after.Should().Be(GuestShutdownEvidence.ObservedPanic);
    }

    [Fact]
    public void AnOrderlyGuestShutdownOutranksEverythingSeenLater()
    {
        QmpShutdownEvidence.Strongest(
            GuestShutdownEvidence.ObservedGuestInitiated,
            GuestShutdownEvidence.ProcessExitOnly)
            .Should().Be(GuestShutdownEvidence.ObservedGuestInitiated);
    }
}
