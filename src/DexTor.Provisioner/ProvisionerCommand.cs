using DexTor.Contracts;

namespace DexTor.Provisioner;

/// <summary>
/// The elevated USB creation command, callable from any host executable.
/// </summary>
/// <remarks>
/// It is the whole of what <c>DexTor.Provisioner.exe</c> does, kept in a class so the
/// portable launcher can run it too: the single-file launcher restarts itself elevated
/// with <see cref="ModeArgument"/> instead of shipping a second executable beside it.
/// </remarks>
public static class ProvisionerCommand
{
    /// <summary>The first argument that switches a host executable into this command.</summary>
    public const string ModeArgument = "--provision";

    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var requestPath = GetArgument(args, "--request");
        var resultPath = GetArgument(args, "--result");
        var progressPath = GetArgument(args, "--progress");
        if (GetArgument(args, "--journal") is { Length: > 0 } journal)
        {
            HybridUsbProvisioner.JournalDirectory = Path.GetFullPath(journal);
        }
        var execute = args.Contains("--execute", StringComparer.OrdinalIgnoreCase);
        // The wizard asks to stop by creating this file: the launcher runs unelevated and
        // cannot signal the elevated process any other way. The provisioner stops at the
        // next cancellation point, releases its volume locks and reports the interruption.
        using var cancellation = new CancellationTokenSource();
        using var cancelWatch = WatchForCancelFile(GetArgument(args, "--cancel"), cancellation);
        if (string.IsNullOrWhiteSpace(requestPath) || string.IsNullOrWhiteSpace(resultPath))
        {
            return 2;
        }

        UsbProvisioningResult result;
        try
        {
            var request = UsbProvisioningSerializer.DeserializeRequest(await File.ReadAllTextAsync(requestPath).ConfigureAwait(false));
            if (request is null)
            {
                return 2;
            }

            var throttle = new ByteProgressThrottle(TimeSpan.FromMilliseconds(250));
            result = execute
                ? await new HybridUsbProvisioner(
                    new PowerShellPhysicalDiskInventory(),
                    new DiskPartExecutor(),
                    new PhysicalHybridDiskWriter(),
                    stage => WriteProgress(progressPath, request.RequestId, stage),
                    bytesChanged: (stage, done, total) =>
                    {
                        if (throttle.ShouldReport(done, total))
                        {
                            WriteProgress(progressPath, request.RequestId, stage, done, total);
                        }
                    }).ExecuteAsync(request, cancellation.Token).ConfigureAwait(false)
                : new UsbProvisioningResult(
                    request.RequestId,
                    UsbProvisioningStatus.Rejected,
                    "The explicit --execute switch is required.");
        }
        catch (Exception exception)
        {
            result = new UsbProvisioningResult(Guid.Empty, UsbProvisioningStatus.Rejected, exception.Message);
        }

        var fullResultPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullResultPath)!);
        await File.WriteAllTextAsync(fullResultPath, UsbProvisioningSerializer.SerializeResult(result)).ConfigureAwait(false);
        return result.Success ? 0 : 1;
    }

    /// <summary>Cancels <paramref name="cancellation"/> once <paramref name="path"/> exists.</summary>
    public static Timer? WatchForCancelFile(string? path, CancellationTokenSource cancellation)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var fullPath = Path.GetFullPath(path);
        return new Timer(_ =>
        {
            if (File.Exists(fullPath))
            {
                try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(300));
    }

    /// <summary>
    /// Lets through a byte report a few times a second, and always the last one of a pass.
    /// </summary>
    internal sealed class ByteProgressThrottle(TimeSpan interval)
    {
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan _last = TimeSpan.FromDays(-1);

        internal bool ShouldReport(long done, long total)
        {
            var now = _clock.Elapsed;
            if (done < total && now - _last < interval) return false;
            _last = now;
            return true;
        }
    }

    private static void WriteProgress(string? path, Guid requestId, UsbProvisioningStage stage, long bytesDone = 0, long bytesTotal = 0)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporaryPath = fullPath + ".tmp";
            File.WriteAllText(temporaryPath, UsbProvisioningSerializer.SerializeProgress(
                new UsbProvisioningProgress(requestId, stage, DateTimeOffset.UtcNow, bytesDone, bytesTotal)));
            File.Move(temporaryPath, fullPath, true);
        }
        catch (IOException)
        {
            // Progress reporting is best-effort and must never interrupt disk preparation.
        }
        catch (UnauthorizedAccessException)
        {
            // The operation can still complete even if the UI progress file is unavailable.
        }
    }

    private static string? GetArgument(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
