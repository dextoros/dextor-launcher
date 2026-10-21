using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace DexTor.Launcher;

internal sealed record QemuRuntimeInspectionResult(bool IsReady, string RuntimePath, string? ErrorMessage = null);

internal sealed class QemuRuntimePreparationService
{
    internal const string Version = "11.1.0";
    internal const string StageDownload = "launcher.create.runtime.prepare.status.downloading";
    internal const string StageVerify = "launcher.create.runtime.prepare.status.verifying";
    internal const string StageInstall = "launcher.create.runtime.prepare.status.installing";
    internal const string InstallerUrl = "https://qemu.weilnetz.de/w64/2026/qemu-w64-setup-20260811.exe";
    internal const string InstallerSha512 = "5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543";

    private readonly HttpClient _httpClient;
    private readonly Func<string, string, CancellationToken, Task<int>> _install;
    private readonly string _installerUrl;
    private readonly string _installerSha512;
    private readonly string _bundledRuntimePath;

    internal QemuRuntimePreparationService(
        HttpClient? httpClient = null,
        Func<string, string, CancellationToken, Task<int>>? install = null,
        string? installerUrl = null,
        string? installerSha512 = null,
        string? bundledRuntimePath = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _install = install ?? InstallSilentlyAsync;
        _installerUrl = installerUrl ?? InstallerUrl;
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

        Directory.CreateDirectory(cacheRoot);
        var installerPath = Path.Combine(cacheRoot, $"qemu-w64-{Version}.exe");
        progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageDownload));
        if (!await DownloadAsync(installerPath, progress, cancellationToken))
        {
            return inspection with { ErrorMessage = "Could not download the QEMU runtime." };
        }

        progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageVerify));
        var actualHash = await ComputeSha512Async(installerPath, cancellationToken);
        if (!string.Equals(actualHash, _installerSha512, StringComparison.OrdinalIgnoreCase))
        {
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

    private async Task<bool> DownloadAsync(
        string destinationPath,
        IProgress<LiveImagePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath)
            && string.Equals(await ComputeSha512Async(destinationPath, cancellationToken), _installerSha512, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var temporaryPath = destinationPath + ".tmp";
        TryDelete(temporaryPath);
        try
        {
            using var response = await _httpClient.GetAsync(_installerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = File.Create(temporaryPath);
            var buffer = new byte[64 * 1024];
            var started = DateTime.UtcNow;
            long downloaded = 0;
            var lastReport = DateTime.MinValue;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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
            await destination.FlushAsync(cancellationToken);
            destination.Close();
            TryDelete(destinationPath);
            File.Move(temporaryPath, destinationPath);
            return true;
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

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
