using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DexTor.Qemu;

public sealed class QmpStreamChannel(TextReader reader, TextWriter writer) : IQmpChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task HandshakeAsync(CancellationToken cancellationToken = default)
    {
        var greeting = await ReadObjectAsync(cancellationToken).ConfigureAwait(false);
        if (!greeting.RootElement.TryGetProperty("QMP", out _))
        {
            throw new IOException("QEMU did not return a valid QMP greeting.");
        }

        await WriteCommandAsync("qmp_capabilities", cancellationToken).ConfigureAwait(false);
        await ReadSuccessfulResponseAsync(cancellationToken).ConfigureAwait(false);
        await WriteCommandAsync("query-version", cancellationToken).ConfigureAwait(false);
        await ReadSuccessfulResponseAsync(cancellationToken).ConfigureAwait(false);
        var status = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        if (status is "paused" or "prelaunch")
        {
            await WriteCommandAsync("cont", cancellationToken).ConfigureAwait(false);
            await ReadSuccessfulResponseAsync(cancellationToken).ConfigureAwait(false);
            status = await QueryStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(status, "running", StringComparison.Ordinal))
        {
            throw new IOException($"QEMU virtual machine did not enter the running state (status: {status}).");
        }
    }

    public async IAsyncEnumerable<QmpEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var document = await ReadObjectAsync(cancellationToken).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("event", out var eventName))
            {
                continue;
            }

            document.RootElement.TryGetProperty("data", out var data);
            document.RootElement.TryGetProperty("timestamp", out var timestamp);
            yield return new QmpEvent(
                eventName.GetString() ?? string.Empty,
                data.ValueKind == JsonValueKind.Undefined ? null : data.Clone(),
                timestamp.ValueKind == JsonValueKind.Undefined ? null : timestamp.GetRawText());
        }
    }

    public async Task SendAsync(QmpFrame frame, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var payload = JsonSerializer.Serialize(new
        {
            execute = frame.Command,
            arguments = frame.Arguments.RootElement
        }, JsonOptions);
        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        writer.Dispose();
        reader.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task WriteCommandAsync(string command, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { execute = command }, JsonOptions);
        await writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadSuccessfulResponseAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var response = await ReadObjectAsync(cancellationToken).ConfigureAwait(false);
            if (response.RootElement.TryGetProperty("error", out var error))
            {
                throw new IOException($"QMP command failed: {error.GetRawText()}");
            }
            if (response.RootElement.TryGetProperty("return", out _))
            {
                return;
            }
        }
    }

    private async Task<string> QueryStatusAsync(CancellationToken cancellationToken)
    {
        await WriteCommandAsync("query-status", cancellationToken).ConfigureAwait(false);
        while (true)
        {
            using var response = await ReadObjectAsync(cancellationToken).ConfigureAwait(false);
            if (response.RootElement.TryGetProperty("error", out var error))
            {
                throw new IOException($"QMP command failed: {error.GetRawText()}");
            }
            if (response.RootElement.TryGetProperty("return", out var result)
                && result.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String)
            {
                return status.GetString() ?? "unknown";
            }
        }
    }

    private async Task<JsonDocument> ReadObjectAsync(CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (line is null)
        {
            throw new EndOfStreamException("QMP channel closed unexpectedly.");
        }
        return JsonDocument.Parse(line);
    }
}
