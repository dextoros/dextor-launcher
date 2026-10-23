using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Contracts;

namespace DexTor.Launcher;

internal sealed record QemuRuntimeInspectionResult(bool IsReady, string RuntimePath, string? ErrorMessage = null);

internal sealed partial class QemuRuntimePreparationService
{
    internal static string Version => QemuRuntimeDistribution.Default.Version;
    internal const string StageDownload = "launcher.create.runtime.prepare.status.downloading";
    internal const string StageVerify = "launcher.create.runtime.prepare.status.verifying";
    internal const string StageInstall = "launcher.create.runtime.prepare.status.installing";
    internal static string InstallerUrl => QemuRuntimeDistribution.Default.InstallerUrls[0];
    internal static string InstallerSha512 => QemuRuntimeDistribution.Default.InstallerSha512;
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly Func<string, string, CancellationToken, Task<int>> _install;
    private readonly IReadOnlyList<string> _installerUrls;
    private readonly string _installerSha512;
    private readonly string _bundledRuntimePath;
    private readonly Func<string, Stream> _openDownloadFile;

    internal QemuRuntimePreparationService(
        HttpClient? httpClient = null,
        Func<string, string, CancellationToken, Task<int>>? install = null,
        string? installerUrl = null,
        string? installerSha512 = null,
        string? bundledRuntimePath = null,
        IReadOnlyList<string>? fallbackInstallerUrls = null,
        Func<string, Stream>? openDownloadFile = null)
    {
        _httpClient = httpClient ?? CreateHttpClient();
        _openDownloadFile = openDownloadFile ?? OpenDownloadFile;
        _install = install ?? InstallSilentlyAsync;
        _installerUrls = installerUrl is null
            ? QemuRuntimeDistribution.Default.InstallerUrls
            : new[] { installerUrl }.Concat(fallbackInstallerUrls ?? []).Distinct(StringComparer.Ordinal).ToArray();
        _installerSha512 = installerSha512 ?? InstallerSha512;
        _bundledRuntimePath = bundledRuntimePath ?? Path.Combine(AppContext.BaseDirectory, "runtime", "qemu");
    }

    internal QemuRuntimeInspectionResult Inspect(string cacheRoot)
    {
        if (HasBundledRuntimeLayout(_bundledRuntimePath))
        {
            return new QemuRuntimeInspectionResult(true, _bundledRuntimePath);
        }

        var runtimePath = GetRuntimePath(cacheRoot);
        var markerPath = Path.Combine(runtimePath, ".dextor-runtime.sha512");
        var ready = HasRequiredRuntimeLayout(runtimePath)
            && File.Exists(markerPath)
            && string.Equals(File.ReadAllText(markerPath).Trim(), _installerSha512, StringComparison.OrdinalIgnoreCase)
            && IsComplete(runtimePath);
        return new QemuRuntimeInspectionResult(ready, runtimePath);
    }

    internal async Task<QemuRuntimeInspectionResult> PrepareAsync(
        string cacheRoot,
        IProgress<LiveImagePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var inspection = Inspect(cacheRoot);
        if (inspection.IsReady)
        {
            return inspection;
        }

        if (MustNotDownload(inspection, out var packagedFailure))
        {
            return packagedFailure;
        }

        Directory.CreateDirectory(cacheRoot);
        var installerPath = Path.Combine(cacheRoot, $"qemu-w64-{Version}.exe");
        progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageDownload));
        if (await DownloadAsync(installerPath, progress, cancellationToken) is { } downloadError)
        {
            return inspection with { ErrorMessage = downloadError };
        }

        progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageVerify));
        var actualHash = await ComputeSha512Async(installerPath, cancellationToken);
        if (!string.Equals(actualHash, _installerSha512, StringComparison.OrdinalIgnoreCase))
        {
            // Every URL serves the same pinned file, so a wrong one is not retried elsewhere:
            // it is deleted, nothing is installed, and the preparation fails.
            TryDelete(installerPath);
            return inspection with { ErrorMessage = "The QEMU runtime checksum does not match." };
        }

        var stagingPath = inspection.RuntimePath + ".staging-" + Guid.NewGuid().ToString("N");
        progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageInstall));
        try
        {
            var exitCode = await _install(installerPath, stagingPath, cancellationToken);
            if (exitCode != 0
                || !File.Exists(Path.Combine(stagingPath, "qemu-system-x86_64.exe"))
                || !Directory.Exists(Path.Combine(stagingPath, "share")))
            {
                return inspection with { ErrorMessage = $"QEMU runtime extraction failed with code {exitCode}." };
            }

            await File.WriteAllTextAsync(
                Path.Combine(stagingPath, ".dextor-runtime.sha512"),
                _installerSha512 + Environment.NewLine,
                cancellationToken);
            WriteFileList(stagingPath);
            if (Directory.Exists(inspection.RuntimePath))
            {
                Directory.Delete(inspection.RuntimePath, recursive: true);
            }
            Directory.Move(stagingPath, inspection.RuntimePath);
            // The unpacked runtime is marked with the installer's hash; the ~200 MB
            // installer itself is not needed again.
            TryDelete(installerPath);
            return Inspect(cacheRoot);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == ErrorElevationRequired)
        {
            return inspection with
            {
                ErrorMessage = "Windows refused to unpack the virtualization runtime without administrator rights."
            };
        }
        finally
        {
            // Whatever the installer registered on the way - shortcuts, an uninstall entry -
            // points into this cache and goes, whether or not the unpack succeeded.
            QemuInstallerTraces.Remove(cacheRoot);
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// Puts the installer at <paramref name="destinationPath"/>, trying the URLs in order.
    /// </summary>
    /// <returns><see langword="null"/> once a complete file is there, otherwise why not.</returns>
    /// <remarks>
    /// Only the source being unreachable moves on to the next URL: DNS, connect, TLS, a
    /// non-2xx answer, a timeout, or a stream that broke or ended early. A file this PC
    /// cannot write would fail the same way from every mirror, and the user's cancellation
    /// stops at once. A completed download is never weighed against another URL - it goes
    /// to the hash check, and a mismatch there ends the preparation.
    /// </remarks>
    private async Task<string?> DownloadAsync(
        string destinationPath,
        IProgress<LiveImagePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)
            && string.Equals(await ComputeSha512Async(destinationPath, cancellationToken), _installerSha512, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var temporaryPath = destinationPath + ".tmp";
        foreach (var url in _installerUrls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (await DownloadFromUrlAsync(url, temporaryPath, progress, cancellationToken))
                {
                    await WriteLocallyAsync(() =>
                    {
                        TryDelete(destinationPath);
                        File.Move(temporaryPath, destinationPath);
                        return Task.CompletedTask;
                    });
                    return null;
                }
            }
            catch (LocalWriteException exception)
            {
                return $"Could not save the QEMU runtime download: {exception.Message}";
            }
            catch (HttpRequestException) { }
            catch (TimeoutException) { }
            // A header or idle timeout of this URL; the user's own cancellation is not caught.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            finally { TryDelete(temporaryPath); }
        }
        return "Could not download the QEMU runtime from any of its sources.";
    }

    /// <returns><see langword="false"/> when this URL answered without the file.</returns>
    private async Task<bool> DownloadFromUrlAsync(
        string url, string temporaryPath,
        IProgress<LiveImagePreparationProgress>? progress, CancellationToken cancellationToken)
    {
        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headersTimeout.CancelAfter(DownloadIdleTimeout);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
        headersTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
        if (!response.IsSuccessStatusCode) return false;
        // GitHub answers a release download with a redirect to its object storage. The
        // handler follows it and never downgrades to http; this only makes that explicit.
        if (response.RequestMessage?.RequestUri is { } finalUri && finalUri.Scheme != Uri.UriSchemeHttps) return false;

        var total = response.Content.Headers.ContentLength;
        Stream source;
        try { source = await response.Content.ReadAsStreamAsync(cancellationToken); }
        catch (IOException exception) { throw new HttpRequestException("The QEMU download stream failed.", exception); }
        await using var sourceScope = source;
        Stream? destination = null;
        try
        {
            await WriteLocallyAsync(() => { destination = _openDownloadFile(temporaryPath); return Task.CompletedTask; });
            var buffer = new byte[64 * 1024];
            var started = DateTime.UtcNow;
            long downloaded = 0;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await ReadNetworkAsync(source, buffer, cancellationToken)) > 0)
            {
                await WriteLocallyAsync(() => destination!.WriteAsync(buffer.AsMemory(0, read), cancellationToken).AsTask());
                downloaded += read;
                // One report per 64 KB flooded the UI thread with thousands of posts; ten
                // a second is as smooth on screen and leaves the pump free to paint.
                var reportedAt = DateTime.UtcNow;
                if (reportedAt - lastReport < TimeSpan.FromMilliseconds(100) && (total is not { } whole || downloaded < whole))
                {
                    continue;
                }
                lastReport = reportedAt;
                var elapsed = reportedAt - started;
                var speed = elapsed.TotalSeconds > 0 ? downloaded / elapsed.TotalSeconds : (double?)null;
                TimeSpan? eta = total is { } size && speed > 0
                    ? TimeSpan.FromSeconds(Math.Max(0, size - downloaded) / speed.Value)
                    : null;
                progress?.Report(new LiveImagePreparationProgress(downloaded, total, speed, eta, StageDownload));
            }
            if (total is { } expected && downloaded != expected)
            {
                throw new HttpRequestException($"The QEMU download ended after {downloaded} of {expected} bytes.");
            }
            var written = destination!;
            destination = null;
            await WriteLocallyAsync(async () =>
            {
                await using (written)
                {
                    await written.FlushAsync(cancellationToken);
                }
            });
            return true;
        }
        finally
        {
            if (destination is not null)
            {
                try { await destination.DisposeAsync(); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Already failing; the error that got here is the one reported.
                }
            }
        }
    }

    private static async Task<int> ReadNetworkAsync(Stream source, byte[] buffer, CancellationToken cancellationToken)
    {
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleTimeout.CancelAfter(DownloadIdleTimeout);
        try { return await source.ReadAsync(buffer, idleTimeout.Token); }
        catch (IOException exception) { throw new HttpRequestException("The QEMU download stream failed.", exception); }
    }

    /// <summary>Marks a failure of this PC's own disk, which no other URL can fix.</summary>
    private static async Task WriteLocallyAsync(Func<Task> write)
    {
        try { await write(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new LocalWriteException(exception);
        }
    }

    // Unbuffered: a full disk fails the write that hit it, not a flush hidden in dispose.
    private static FileStream OpenDownloadFile(string path) =>
        new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0, useAsync: true);

    /// <summary>
    /// GitHub serves a release asset through a redirect to its object storage, so redirects
    /// are followed - a few, and .NET never follows one from https to http.
    /// </summary>
    private static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        ConnectTimeout = DownloadIdleTimeout
    });

    private sealed class LocalWriteException(Exception inner) : Exception(inner.Message, inner);

    private const int ErrorElevationRequired = 740;

    /// <summary>
    /// Unpacks the official QEMU installer into <paramref name="destinationPath"/>.
    /// </summary>
    /// <remarks>
    /// The installer's manifest demands administrator rights, so Windows with UAC
    /// refused to start it from the launcher at all ("The requested operation requires
    /// elevation"). Nothing it does here needs them: it writes into DexTor's own folder.
    /// RunAsInvoker starts it with the launcher's own rights instead of asking for more;
    /// the machine-wide registration it then cannot write is exactly what should not be
    /// written, and what it can write per user is removed afterwards.
    /// </remarks>
    private static async Task<int> InstallSilentlyAsync(string installerPath, string destinationPath, CancellationToken cancellationToken)
    {
        using var process = Process.Start(BuildInstallerStartInfo(installerPath, destinationPath))
            ?? throw new InvalidOperationException("Could not start QEMU runtime extraction.");
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process finished between the state check and Kill.
            }
        });
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    internal static ProcessStartInfo BuildInstallerStartInfo(string installerPath, string destinationPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? Path.GetTempPath()
        };
        startInfo.Environment["__COMPAT_LAYER"] = "RunAsInvoker";
        startInfo.ArgumentList.Add("/S");
        startInfo.ArgumentList.Add($"/D={destinationPath}");
        return startInfo;
    }

    private static string GetRuntimePath(string cacheRoot) => Path.Combine(cacheRoot, $"qemu-{Version}");

    internal const string FileListName = ".dextor-runtime-files.json";

    /// <summary>
    /// Records every file of a freshly unpacked runtime with its size.
    /// </summary>
    /// <remarks>
    /// The runtime lives in %TEMP%, and Windows clears that folder on its own - Storage
    /// Sense and Disk Cleanup delete old files there. On one PC it had taken all but three
    /// entries of a 1.2 GB runtime. Checking for qemu-system-x86_64.exe and share alone
    /// would call a runtime with missing DLLs ready, and QEMU would then fail to start;
    /// this list is what the check compares against.
    /// </remarks>
    internal static void WriteFileList(string runtimePath)
    {
        var root = Path.GetFullPath(runtimePath);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Where(file => !file.Name.StartsWith(".dextor-", StringComparison.Ordinal))
            .Select(file => new RuntimeFile(Path.GetRelativePath(root, file.FullName), file.Length))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        File.WriteAllText(Path.Combine(root, FileListName), JsonSerializer.Serialize(files));
    }

    /// <summary>
    /// Whether every recorded file is still there, at its recorded size. Fast enough to run
    /// before every start: it only asks the file system, it does not hash 1.2 GB.
    /// </summary>
    internal static bool IsComplete(string runtimePath)
    {
        try
        {
            var root = Path.GetFullPath(runtimePath);
            var listPath = Path.Combine(root, FileListName);
            if (!File.Exists(listPath)) return false; // unpacked by an older launcher: nothing to check against
            var files = JsonSerializer.Deserialize<RuntimeFile[]>(File.ReadAllText(listPath));
            if (files is null || files.Length == 0) return false;
            foreach (var file in files)
            {
                var info = new FileInfo(Path.Combine(root, file.Path));
                if (!info.Exists || info.Length != file.Size) return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return false;
        }
    }

    internal sealed record RuntimeFile(string Path, long Size);

    private static bool HasRequiredRuntimeLayout(string runtimePath) =>
        File.Exists(Path.Combine(runtimePath, "qemu-system-x86_64.exe"))
        && Directory.Exists(Path.Combine(runtimePath, "share"));

    private static bool HasBundledRuntimeLayout(string runtimePath)
    {
        if (!HasRequiredRuntimeLayout(runtimePath))
        {
            return false;
        }

        var manifestPath = Path.Combine(runtimePath, "qemu-runtime-files.json");
        try
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            return manifest.RootElement.TryGetProperty("version", out var version)
                && string.Equals(version.GetString(), Version, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeSha512Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLower(CultureInfo.InvariantCulture);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A later retry will validate or replace the cached file.
        }
    }
}
