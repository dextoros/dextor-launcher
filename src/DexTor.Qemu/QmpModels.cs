using System.Text.Json.Serialization;
using System.Text.Json;

namespace DexTor.Qemu;

public sealed record QmpFrame(string Command, JsonDocument Arguments);

public static class QmpCommands
{
    public static QmpFrame QueryVersion() => new("query-version", JsonDocument.Parse("{}"));
    public static QmpFrame Quit() => new("quit", JsonDocument.Parse("{}"));
    public static QmpFrame PowerButton() => new("system_powerdown", JsonDocument.Parse("{}"));
}

public interface IQmpChannel : IAsyncDisposable
{
    IAsyncEnumerable<QmpEvent> ReadAsync(CancellationToken cancellationToken = default);
    Task SendAsync(QmpFrame frame, CancellationToken cancellationToken = default);
}

public sealed record QmpEvent(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("data")] JsonElement? Data,
    [property: JsonPropertyName("timestamp")] string? Timestamp = null);
