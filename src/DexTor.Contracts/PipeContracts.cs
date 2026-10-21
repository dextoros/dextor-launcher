using System.Text.Json;
using System.Text.Json.Serialization;

namespace DexTor.Contracts;

public enum MessageKind
{
    HostEvent,
    SessionRequest,
    SessionResponse,
    Error,
    Diagnostics,
}

public sealed class MessageEnvelope
{
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");
    public MessageKind Kind { get; init; }
    public string Type { get; init; } = string.Empty;
    public JsonElement? Payload { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}

public static class MessageSerializer
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Serialize(MessageEnvelope envelope) => JsonSerializer.Serialize(envelope, _json);

    public static MessageEnvelope? Deserialize(string json) => JsonSerializer.Deserialize<MessageEnvelope>(json, _json);
}
