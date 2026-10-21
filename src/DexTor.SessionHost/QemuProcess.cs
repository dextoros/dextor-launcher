using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DexTor.SessionHost;

/// <summary>
/// The QEMU process and its redirected streams, as the session host observes them.
/// </summary>
/// <remarks>
/// Deliberately the same surface <see cref="System.Diagnostics.Process"/> offered the
/// session host before, so the lifecycle code around it is unchanged. What differs is
/// underneath: identity is the original process handle returned by
/// <c>CreateProcessW</c>, never a later lookup by process id. Holding that handle also
/// stops Windows reusing the id while the session runs, which is what makes the
/// cosmetic window search in <see cref="QemuWindowManager"/> safe.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class QemuProcess : IQemuProcess
{
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint WaitFailed = 0xFFFFFFFF;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint StillActive = 259;

    private readonly SafeProcessHandle _handle;
    private readonly QemuJobObject _job;
    private readonly object _gate = new();
    private RegisteredWaitHandle? _exitRegistration;
    private ManualResetEvent? _exitWaitHandle;
    private EventHandler? _exited;
    private bool _disposed;

    internal QemuProcess(
        int id,
        SafeProcessHandle handle,
        QemuJobObject job,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError)
    {
        Id = id;
        _handle = handle;
        _job = job;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public int Id { get; }

    public StreamWriter StandardInput { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public bool HasExited => WaitForHandle(_handle, TimeSpan.Zero);

    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(_handle, out var code))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The QEMU exit code could not be read.");
            }

            if (code == StillActive && !HasExited)
            {
                throw new InvalidOperationException("QEMU has not exited.");
            }

            return unchecked((int)code);
        }
    }

    /// <summary>
    /// Raised once, on a thread pool thread, when the process object becomes signalled.
    /// </summary>
    /// <remarks>
    /// Registered against the process handle rather than polled, so it reports the
    /// kernel's own view of termination and cannot be fooled by a reused process id.
    /// </remarks>
    public event EventHandler? Exited
    {
        add
        {
            lock (_gate)
            {
                _exited += value;
                EnsureExitWatch();
            }
        }
        remove
        {
            lock (_gate)
            {
                _exited -= value;
            }
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (HasExited)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handle = new ManualResetEvent(false);
        handle.SafeWaitHandle = new SafeWaitHandle(_handle.DangerousGetHandle(), ownsHandle: false);
        using var registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true).AsDisposable();
        await using var cancellation = cancellationToken.UnsafeRegister(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(),
            completion);

        await completion.Task.ConfigureAwait(false);
        GC.KeepAlive(_handle);
    }

    /// <summary>
    /// Blocks until the process exits or the timeout elapses, and reports which.
    /// </summary>
    public bool WaitForExit(TimeSpan timeout) => WaitForHandle(_handle, timeout);

    /// <summary>
    /// Terminates the whole session tree by terminating the job it is defined by, so a
    /// descendant cannot survive by re-parenting.
    /// </summary>
    public void Kill() => _job.Terminate(1);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _exited = null;
            _exitRegistration?.Unregister(null);
            _exitRegistration = null;
            _exitWaitHandle?.Dispose();
            _exitWaitHandle = null;
        }

        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _handle.Dispose();
    }

    /// <summary>
    /// Waits on a process handle. Shared with the launcher so a child that could not be
    /// handed over is confirmed gone by exactly the same evidence.
    /// </summary>
    internal static bool WaitForHandle(SafeProcessHandle handle, TimeSpan timeout)
    {
        var milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? Infinite
            : (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
        var result = WaitForSingleObject(handle, milliseconds);
        return result switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            WaitFailed => throw new Win32Exception(
                Marshal.GetLastPInvokeError(),
                "The QEMU process could not be waited on."),
            _ => true
        };
    }

    private void EnsureExitWatch()
    {
        if (_exitRegistration is not null || _disposed)
        {
            return;
        }

        var waitHandle = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(_handle.DangerousGetHandle(), ownsHandle: false)
        };
        _exitWaitHandle = waitHandle;
        _exitRegistration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, _) => ((QemuProcess)state!).RaiseExited(),
            this,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
    }

    private void RaiseExited()
    {
        EventHandler? handlers;
        lock (_gate)
        {
            handlers = _exited;
        }

        handlers?.Invoke(this, EventArgs.Empty);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);
}

internal static class RegisteredWaitHandleExtensions
{
    /// <summary>
    /// Adapts a registration to <c>using</c>, so the wait is always unregistered.
    /// </summary>
    public static IDisposable AsDisposable(this RegisteredWaitHandle registration) =>
        new Unregisterer(registration);

    private sealed class Unregisterer(RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose() => registration.Unregister(null);
    }
}
