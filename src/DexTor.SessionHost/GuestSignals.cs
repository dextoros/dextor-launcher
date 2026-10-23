using System.IO.Pipes;
using System.Text;

namespace DexTor.SessionHost;

/// <summary>What DexTor OS tells the launcher about its own start.</summary>
public enum GuestSignal
{
    /// <summary>"frame": the guest has drawn a picture worth showing (its splash).</summary>
    Frame,

    /// <summary>"desktop": the desktop is up; the start is over.</summary>
    Desktop
}

/// <summary>
/// Reads DexTor OS's start signals from its virtio-serial port (see
/// <c>QemuLaunchPlanBuilder.ControlPortName</c>), which QEMU serves on a named pipe.
/// </summary>
/// <remarks>
/// <para>
/// The VM window is shown when the guest has a picture. Looking at screendumps tells that
/// only every few hundred milliseconds, and only as far as pixels go; DexTor OS knows when its
/// splash is up and when its desktop is. It writes one word per line - "frame", "desktop" -
/// and the launcher acts on it at once. Screendumps stay as the fallback for images that do
/// not send signals yet.
/// </para>
/// <para>
/// QEMU waits for this pipe to be connected before it starts the guest, so the connection is
/// made right after QEMU starts. The launcher never writes to the port: nothing goes from the
/// host into the guest this way. Anything but the known words is ignored, and lines are capped.
/// </para>
/// </remarks>
public sealed class GuestSignals : IAsyncDisposable
{
    private const int MaxLineLength = 64;
    private readonly NamedPipeClientStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private Task? _reader;
    private readonly Lock _gate = new();
    private volatile bool _frame;
    private volatile bool _desktop;
    private TaskCompletionSource _frameSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _desktopSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private GuestSignals(string pipeName)
    {
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.In, PipeOptions.Asynchronous);
    }

    /// <summary>Signals that are never connected, for tests that feed lines to <see cref="Handle"/>.</summary>
    internal static GuestSignals Unconnected() => new($"dextor-ctl-unconnected-{Guid.NewGuid():N}");

    /// <summary>The guest has said it draws.</summary>
    public bool FrameReady => _frame;

    /// <summary>The guest has said its desktop is up.</summary>
    public bool DesktopReady => _desktop;

    /// <summary>Completes when "frame" arrives: the window opens at that moment, not at the next look.</summary>
    public Task Frame
    {
        get { lock (_gate) return _frameSeen.Task; }
    }

    /// <summary>Completes when "desktop" arrives.</summary>
    public Task Desktop
    {
        get { lock (_gate) return _desktopSeen.Task; }
    }

    /// <summary>
    /// The guest has restarted (QMP RESET): what it said before belongs to the boot that ended.
    /// It boots again from the firmware and says "frame" and "desktop" again; until then the
    /// launcher treats it as starting.
    /// </summary>
    public void Restart()
    {
        lock (_gate)
        {
            _frame = false;
            _desktop = false;
            if (_frameSeen.Task.IsCompleted) _frameSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_desktopSeen.Task.IsCompleted) _desktopSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Raised on the reader's thread for every signal.</summary>
    public event Action<GuestSignal>? Received;

    /// <summary>Connects to QEMU's pipe, waiting up to <paramref name="timeout"/> for it to appear.</summary>
    public static async Task<GuestSignals> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var signals = new GuestSignals(pipeName);
        try
        {
            await signals._pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await signals.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        signals._reader = Task.Run(signals.ReadAsync, CancellationToken.None);
        return signals;
    }

    /// <summary>The signal a line names, or null for anything else.</summary>
    public static GuestSignal? Parse(string line) => line.Trim().ToLowerInvariant() switch
    {
        "frame" => GuestSignal.Frame,
        "desktop" => GuestSignal.Desktop,
        _ => null
    };

    private async Task ReadAsync()
    {
        var buffer = new byte[256];
        var line = new StringBuilder();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var read = await _pipe.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0) return;
                foreach (var b in buffer.AsSpan(0, read))
                {
                    if (b == (byte)'\n')
                    {
                        Handle(line.ToString());
                        line.Clear();
                    }
                    else if (line.Length < MaxLineLength && b is >= 0x20 and < 0x7F)
                    {
                        line.Append((char)b);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // QEMU went away or the session ended.
        }
    }

    internal void Handle(string line)
    {
        if (Parse(line) is not { } signal) return;
        lock (_gate)
        {
            if (signal == GuestSignal.Frame) _frame = true;
            if (signal == GuestSignal.Desktop) _desktop = _frame = true;
            _frameSeen.TrySetResult();
            if (signal == GuestSignal.Desktop) _desktopSeen.TrySetResult();
        }

        Received?.Invoke(signal);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pipe.DisposeAsync().ConfigureAwait(false);
        if (_reader is { } reader)
        {
            try { await reader.ConfigureAwait(false); } catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        }

        _stop.Dispose();
    }
}
