namespace DexTor.SessionHost;

/// <summary>
/// The QEMU process lifecycle the session host depends on.
/// </summary>
/// <remarks>
/// This is the seam tests substitute at, and it is deliberately narrow: it can stand in
/// for a process that already exists, but it cannot start one. Creating QEMU outside the
/// session job is not reachable through any seam, in tests or otherwise — that is the
/// whole point of <see cref="QemuProcessLauncher"/>.
/// </remarks>
internal interface IQemuProcess : IDisposable
{
    /// <summary>Process id, for diagnostics only. Identity is the process handle.</summary>
    int Id { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    StreamWriter StandardInput { get; }

    StreamReader StandardOutput { get; }

    StreamReader StandardError { get; }

    event EventHandler? Exited;

    Task WaitForExitAsync(CancellationToken cancellationToken = default);

    /// <summary>Blocks for at most <paramref name="timeout"/>; true when it has exited.</summary>
    bool WaitForExit(TimeSpan timeout);

    /// <summary>Terminates the entire session process tree.</summary>
    void Kill();
}
