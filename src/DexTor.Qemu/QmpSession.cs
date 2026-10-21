using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace DexTor.Qemu;

/// <summary>One QMP reply, as the dispatcher matched it to a command.</summary>
/// <param name="Status">
/// The <c>status</c> field when the reply carried one, as <c>query-status</c> returns.
/// </param>
public sealed record QmpResponse(bool Succeeded, string? Error, string? Status = null)
{
    public static QmpResponse Ok { get; } = new(true, null);
}

/// <summary>
/// The one reader of QEMU's QMP stream, dispatching replies to their commands and events
/// to subscribers.
/// </summary>
/// <remarks>
/// A single loop is not a style preference. QMP interleaves asynchronous events with
/// command replies on one stream, so a second reader started for events would swallow a
/// reply — or a reply-reader would swallow the <c>SHUTDOWN</c> event that is the only
/// evidence the guest shut down on its own. Everything therefore arrives here and is
/// routed.
/// <para>
/// Commands carry an <c>id</c> and are matched on it, rather than assumed to return in
/// order: QEMU echoes the id back, and correlating on it survives an event arriving
/// between a command and its reply.
/// </para>
/// <para>
/// Events are delivered to subscribers registered <em>before</em> shutdown is requested.
/// A final event that QEMU writes just before exiting is still in the pipe when the
/// process dies, so the loop drains to end-of-stream and publishes what it finds; the
/// drain is observable through <see cref="Completion"/>.
/// </para>
/// </remarks>
public sealed class QmpSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<QmpResponse>> _pending = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _pump;
    private long _nextId;
    private int _disposed;

    public QmpSession(TextReader reader, TextWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <summary>
    /// Raised for every event, on the reader thread. Handlers must not block.
    /// </summary>
    public event Action<QmpEvent>? EventReceived;

    /// <summary>Completes when the stream ends or the pump stops.</summary>
    public Task Completion => _completion.Task;

    /// <summary>True once end-of-stream was reached.</summary>
    public bool StreamEnded { get; private set; }

    /// <summary>
    /// Starts the reader. Subscribe to <see cref="EventReceived"/> first, or an event
    /// that arrives immediately is lost.
    /// </summary>
    public void Start() => _pump ??= Task.Run(PumpAsync);

    /// <summary>
    /// Performs the QEMU handshake and leaves the machine running.
    /// </summary>
    public async Task HandshakeAsync(TimeSpan commandTimeout, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync("qmp_capabilities", commandTimeout, cancellationToken).ConfigureAwait(false);
        await ExecuteAsync("query-version", commandTimeout, cancellationToken).ConfigureAwait(false);

        var status = await QueryStatusAsync(commandTimeout, cancellationToken).ConfigureAwait(false);
        if (status is "paused" or "prelaunch")
        {
            await ExecuteAsync("cont", commandTimeout, cancellationToken).ConfigureAwait(false);
            status = await QueryStatusAsync(commandTimeout, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(status, "running", StringComparison.Ordinal))
        {
            throw new IOException($"QEMU virtual machine did not enter the running state (status: {status}).");
        }
    }

    /// <summary>
    /// Sends a command and waits for its reply, bounded by <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// A timeout abandons the wait, not the command: QEMU may still act on it, and the
    /// caller learns only that no reply arrived in time. Escalation treats that as "this
    /// stage did not answer", never as "this stage did nothing".
    /// </remarks>
    public Task<QmpResponse> ExecuteAsync(
        string command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(command, null, timeout, cancellationToken);

    /// <param name="arguments">The command's "arguments" object; null sends none.</param>
    public async Task<QmpResponse> ExecuteAsync(
        string command,
        object? arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (StreamEnded)
        {
            return new QmpResponse(false, "The QMP channel is closed.");
        }

        var id = "dextor-" + Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<QmpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            var payload = arguments is null
                ? JsonSerializer.Serialize(new { execute = command, id }, JsonOptions)
                : JsonSerializer.Serialize(new { execute = command, arguments, id }, JsonOptions);
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _writer.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }

            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new QmpResponse(false, $"QEMU did not answer '{command}' within {timeout.TotalSeconds:0}s.");
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            return new QmpResponse(false, exception.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task<string> QueryStatusAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync("query-status", timeout, cancellationToken).ConfigureAwait(false);
        return response.Succeeded
            ? response.Status ?? "unknown"
            : throw new IOException(response.Error ?? "QEMU did not report its run state.");
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(_shutdown.Token).ConfigureAwait(false);
                if (line is null)
                {
                    StreamEnded = true;
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                Dispatch(line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or JsonException)
        {
            StreamEnded = true;
        }
        finally
        {
            // Nothing else will ever answer these.
            foreach (var pending in _pending.Values)
            {
                pending.TrySetResult(new QmpResponse(false, "The QMP channel closed before a reply arrived."));
            }

            _completion.TrySetResult();
        }
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // QEMU can emit non-JSON noise on stdout; it is not a reason to stop reading.
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.TryGetProperty("event", out var eventName))
            {
                root.TryGetProperty("data", out var data);
                root.TryGetProperty("timestamp", out var timestamp);
                var published = new QmpEvent(
                    eventName.GetString() ?? string.Empty,
                    data.ValueKind == JsonValueKind.Undefined ? null : data.Clone(),
                    timestamp.ValueKind == JsonValueKind.Undefined ? null : timestamp.GetRawText());
                EventReceived?.Invoke(published);
                return;
            }

            // The greeting has neither an id nor a return; it is consumed and ignored.
            if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var id = idElement.GetString();
            if (id is null || !_pending.TryGetValue(id, out var completion))
            {
                return;
            }

            if (root.TryGetProperty("error", out var error))
            {
                completion.TrySetResult(new QmpResponse(false, error.GetRawText()));
                return;
            }

            if (root.TryGetProperty("return", out var result))
            {
                var status = result.ValueKind == JsonValueKind.Object
                    && result.TryGetProperty("status", out var statusElement)
                    && statusElement.ValueKind == JsonValueKind.String
                        ? statusElement.GetString()
                        : null;
                completion.TrySetResult(new QmpResponse(true, null, status));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdown.CancelAsync().ConfigureAwait(false);

        // Closing the streams is what actually unblocks the pump. A pending read on a
        // pipe does not observe a cancellation token, so cancelling alone would leave the
        // await below waiting for a read that will never return.
        _writer.Dispose();
        _reader.Dispose();

        if (_pump is not null)
        {
            try
            {
                // Bounded: the pump has nothing left to do but notice the closed stream,
                // and a reader that somehow does not is not worth blocking shutdown on.
                await _pump.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
            {
            }
        }

        _shutdown.Dispose();
        _writeGate.Dispose();
    }
}
