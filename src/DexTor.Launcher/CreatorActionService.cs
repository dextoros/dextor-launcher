using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using DexTor.Creator;
using DexTor.Release;

namespace DexTor.Launcher;

public static class CreatorActionService
{
    public const string ReleaseSourceEnvVar = "DEXTOR_RELEASE_SOURCE";
    public const string ReleaseSha256EnvVar = "DEXTOR_RELEASE_SHA256";
    public const string ReleaseCacheEnvVar = "DEXTOR_RELEASE_CACHE";
    public const string ReleaseManifestEnvVar = "DEXTOR_RELEASE_MANIFEST";
    public const string DefaultReleaseCacheFile = "system.img";
    public const string DefaultReleaseManifestFile = "release-manifest.json";
    private const string DeviceFilePath = "DEXTOR\\metadata\\device.json";
    private const string TransactionStateFile = "DEXTOR\\metadata\\creator-transaction.json";
    private const string StartHtmlFile = "START DEXTOR ON WINDOWS.html";
    private const string DexTorDownloadUrl = "https://dextoros.com/download.html";
    private const string StartHtmlTemplate =
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>START DEXTOR ON WINDOWS</title></head><body><h1>DexTor</h1><a href=\"" + DexTorDownloadUrl + "\">Open the official DexTor OS download page</a></body></html>";

    public sealed record CreateUsbResult(
        bool Success,
        string UsbRootPath,
        CreatorTransaction Transaction,
        string? ErrorMessage = null,
        CreatorStep? ErrorStep = null,
        string? ErrorMessageKey = null,
        IReadOnlyDictionary<string, string>? ErrorMessageArgs = null,
        string? RecoveryPath = null,
        string? ErrorSafetyMessageKey = null,
        string? HostCachedSystemImagePath = null,
        string? UsbSystemImagePath = null,
        string? DataImagePath = null,
        string? SystemImageSha256 = null,
        string? JournalPath = null);

    public enum CreatorStepStatus
    {
        Info,
        Warning,
        Error,
        Success
    }

    public sealed record CreatorStepProgress(
        CreatorStep Step,
        CreatorStepStatus Status,
        string? MessageKey = null,
        IReadOnlyDictionary<string, string>? MessageArgs = null,
        long BytesDone = 0,
        long BytesTotal = 0);

    public sealed record ReleaseConfig(
        string Source,
        string Sha256,
        string DestinationPath,
        string? ManifestPath = null);

    public static CreatorTransaction CreateTransaction()
    {
        return new CreatorTransaction();
    }

    private sealed record CreatorTransactionSnapshot(
        Guid Id,
        CreatorStep LastCompleted,
        bool IsInterrupted);

    public static string GetTransactionStatePath(string usbRootPath)
    {
        var normalizedRoot = Path.GetFullPath(usbRootPath);
        return Path.Combine(normalizedRoot, TransactionStateFile);
    }

    public static CreatorTransaction? TryLoadTransaction(string usbRootPath)
    {
        try
        {
            var transactionStatePath = GetTransactionStatePath(usbRootPath);
            if (!File.Exists(transactionStatePath))
            {
                return null;
            }

            var payload = File.ReadAllText(transactionStatePath);
            var snapshot = JsonSerializer.Deserialize<CreatorTransactionSnapshot>(payload);
            if (snapshot is null)
            {
                return null;
            }

            return new CreatorTransaction(snapshot.LastCompleted, snapshot.IsInterrupted);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool SaveTransactionState(string usbRootPath, CreatorTransaction transaction)
    {
        try
        {
            WriteTransactionState(GetTransactionStatePath(usbRootPath), transaction);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool HasSetupInterrupted(string usbRootPath)
    {
        if (string.IsNullOrWhiteSpace(usbRootPath))
        {
            return false;
        }

        try
        {
            return TryLoadTransaction(usbRootPath)?.CanResume == true;
        }
        catch
        {
            return false;
        }
    }

    public static string? GetReleaseConfigRecoveryPath()
    {
        var source = Environment.GetEnvironmentVariable(ReleaseSourceEnvVar);
        if (!string.IsNullOrWhiteSpace(source))
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out var sourceUri) && sourceUri.IsFile)
            {
                if (TryResolveFileUri(sourceUri, out var resolvedSource))
                {
                    return resolvedSource;
                }
            }
            else if (Uri.TryCreate(source, UriKind.Absolute, out sourceUri)
                && (sourceUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || sourceUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                return sourceUri.AbsoluteUri;
            }
            else if (File.Exists(source))
            {
                return Path.GetFullPath(source);
            }
        }

        var manifest = Environment.GetEnvironmentVariable(ReleaseManifestEnvVar);
        if (string.IsNullOrWhiteSpace(manifest))
        {
            return null;
        }

        if (Uri.TryCreate(manifest, UriKind.Absolute, out var manifestUri) && manifestUri.IsFile)
        {
            if (TryResolveFileUri(manifestUri, out var resolvedManifest))
            {
                return resolvedManifest;
            }

            var manifestLocalPath = manifestUri.LocalPath;
            if (!string.IsNullOrWhiteSpace(manifestLocalPath) && File.Exists(manifestLocalPath))
            {
                return Path.GetFullPath(manifestLocalPath);
            }

            return null;
        }

        return File.Exists(manifest) ? Path.GetFullPath(manifest) : null;
    }

    public static string GetCreateErrorSolutionKey(string? errorMessageKey)
    {
        if (string.IsNullOrWhiteSpace(errorMessageKey))
        {
            return "launcher.action.create.solution";
        }

        return errorMessageKey.StartsWith("launcher.create.error.release_manifest_", StringComparison.Ordinal)
            ? "launcher.action.create.solution.release_manifest"
            : errorMessageKey == "launcher.create.error.release"
                ? "launcher.action.create.solution.release"
                : errorMessageKey.StartsWith("launcher.create.error.release_", StringComparison.Ordinal)
                ? "launcher.action.create.solution.release_config"
                // A faulty image is not fixed by a different drive, so it does not get the
                // "check the USB and try another one" answer.
                // Stopped on request: nothing is wrong, the stick only has to be made again.
                : errorMessageKey == "launcher.create.error.cancelled"
                ? "launcher.action.create.solution.cancelled"
                : errorMessageKey == "launcher.create.error.image"
                ? "launcher.action.create.solution.image"
                // Each device fault has its own next step; the key carries the kind, so the
                // advice is derived from it rather than kept in a second parallel table.
                : errorMessageKey.StartsWith("launcher.create.error.device.", StringComparison.Ordinal)
                ? "launcher.action.create.solution.device."
                    + errorMessageKey["launcher.create.error.device.".Length..]
                : "launcher.action.create.solution";
    }

    public static string GetCreateWarningSolutionKey(string? warningMessageKey)
    {
        if (string.IsNullOrWhiteSpace(warningMessageKey))
        {
            return "launcher.action.create.warning.solution";
        }

        return warningMessageKey == "launcher.create.warning.verify_usb"
            ? "launcher.action.create.warning.solution.verify_usb"
            : "launcher.action.create.warning.solution";
    }

    public static string GetCreateErrorSafetyKey(CreatorStep? step, string? errorMessageKey)
    {
        if (!step.HasValue)
        {
            return "launcher.create.error.safety.unknown";
        }

        return step == CreatorStep.PrepareRelease
            ? "launcher.create.error.safety.unchanged"
            : "launcher.create.error.safety.possibly_modified";
    }

    public static bool DeleteTransactionState(string usbRootPath)
    {
        try
        {
            var transactionStatePath = GetTransactionStatePath(usbRootPath);
            if (!File.Exists(transactionStatePath))
            {
                return true;
            }

            File.Delete(transactionStatePath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteTransactionState(string transactionStatePath, CreatorTransaction transaction)
    {
        var snapshot = new CreatorTransactionSnapshot(
            transaction.Id,
            transaction.LastCompleted,
            transaction.IsInterrupted);

        var payload = JsonSerializer.Serialize(snapshot);
        var directory = Path.GetDirectoryName(transactionStatePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{transactionStatePath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, payload);
        File.Move(tempPath, transactionStatePath, true);
    }

    public static CreateUsbResult RunCreateFlow(string usbRootPath)
    {
        return RunCreateFlow(usbRootPath, (IProgress<CreatorStep>?)null);
    }

    public static bool TryGetReleaseConfigFromEnvironment(
        out ReleaseConfig? config,
        out string? errorMessageKey)
    {
        config = null;
        errorMessageKey = null;

        var source = Environment.GetEnvironmentVariable(ReleaseSourceEnvVar);
        var sha = Environment.GetEnvironmentVariable(ReleaseSha256EnvVar);
        var manifest = Environment.GetEnvironmentVariable(ReleaseManifestEnvVar);

        if (string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(sha))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(sha))
        {
            errorMessageKey = "launcher.create.error.release_config";
            return false;
        }

        source = source.Trim();
        sha = sha.Trim();

        if (!IsSha256Valid(sha))
        {
            errorMessageKey = "launcher.create.error.release_config";
            return false;
        }

        if (!TryResolveReleaseSource(source, out var resolvedSource))
        {
            errorMessageKey = "launcher.create.error.release_config";
            return false;
        }

        string? resolvedManifest = null;
        if (!string.IsNullOrWhiteSpace(manifest))
        {
            var manifestSource = manifest.Trim();
            if (!TryResolveReleaseManifest(manifestSource, out resolvedManifest))
            {
                errorMessageKey = "launcher.create.error.release_config";
                return false;
            }
        }
        else if (IsLocalFileUriOrPath(resolvedSource) && TryResolveImplicitManifestPath(resolvedSource, out var implicitManifest))
        {
            resolvedManifest = implicitManifest;
        }

        var cacheRoot = Environment.GetEnvironmentVariable(ReleaseCacheEnvVar);
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            cacheRoot = DexTor.Contracts.DexTorStorage.Release;
        }
        else
        {
            cacheRoot = cacheRoot.Trim();
        }

        if (!TryResolveReleaseCache(cacheRoot, out var resolvedCacheRoot))
        {
            errorMessageKey = "launcher.create.error.release_config";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileName(cacheRoot)))
        {
            resolvedCacheRoot = Path.Combine(resolvedCacheRoot, "cache");
        }
        config = new ReleaseConfig(
            resolvedSource,
            sha.Trim(),
            Path.Combine(resolvedCacheRoot, DefaultReleaseCacheFile),
            resolvedManifest);
        return true;
    }

    public static async Task<CreateUsbResult> RunCreateFlowWithReleaseAsync(
        string usbRootPath,
        string releaseSource,
        string releaseSha256,
        string releaseDestinationPath,
        IProgress<CreatorStepProgress>? progress,
        Func<CreatorStep, bool>? failStep,
        int stepDelayMs = 130,
        string? releaseManifestPath = null,
        IReleaseDownloader? releaseDownloader = null,
        CreatorTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(releaseDestinationPath))
        {
            throw new ArgumentException("Release destination path is required.", nameof(releaseDestinationPath));
        }

        transaction ??= CreateTransaction();

        if (string.IsNullOrWhiteSpace(usbRootPath))
        {
            return new CreateUsbResult(
                false,
                string.Empty,
                transaction,
                "USB root path is required.",
                ErrorSafetyMessageKey: "launcher.create.error.safety.unknown");
        }

        var normalizedRoot = Path.GetFullPath(usbRootPath);
        Directory.CreateDirectory(normalizedRoot);
        var transactionStatePath = normalizedRoot;

        var downloader = releaseDownloader ?? new ReleaseDownloader();
        ReleaseManifest? manifest = null;
        var isPreparingRelease = transaction.Current == CreatorStep.PrepareRelease;
        var releaseImageFromCache = false;

        if (isPreparingRelease)
        {
            progress?.Report(new CreatorStepProgress(
                CreatorStep.PrepareRelease,
                CreatorStepStatus.Info,
                MessageKey: "launcher.create.step.prepare_release"));
            if (failStep is not null && failStep(CreatorStep.PrepareRelease))
            {
                transaction.Interrupt();
                SaveTransactionState(normalizedRoot, transaction);
                return new CreateUsbResult(
                    false,
                    normalizedRoot,
                    transaction,
                    "Simulated release preparation failure.",
                    CreatorStep.PrepareRelease,
                    ErrorSafetyMessageKey: GetCreateErrorSafetyKey(CreatorStep.PrepareRelease, "launcher.create.error.simulation"));
            }

            var releaseDownloadResult = await downloader.DownloadAsync(
                releaseSource,
                releaseDestinationPath,
                releaseSha256,
                CancellationToken.None);

            if (!releaseDownloadResult.Success)
            {
                transaction.Interrupt();
                SaveTransactionState(normalizedRoot, transaction);
                progress?.Report(new CreatorStepProgress(
                    CreatorStep.PrepareRelease,
                    CreatorStepStatus.Error,
                    "launcher.create.error.release",
                    new Dictionary<string, string>
                    {
                        ["error"] = releaseDownloadResult.ErrorMessage ?? "Unknown release download error."
                    }));

                return new CreateUsbResult(
                    false,
                    normalizedRoot,
                    transaction,
                    releaseDownloadResult.ErrorMessage,
                    CreatorStep.PrepareRelease,
                    "launcher.create.error.release",
                    null,
                    GetReleaseConfigRecoveryPath(),
                    GetCreateErrorSafetyKey(CreatorStep.PrepareRelease, "launcher.create.error.release"));
            }
            releaseImageFromCache = releaseDownloadResult.FromCache;

            if (!TryReadReleaseManifest(
                    releaseManifestPath,
                    out manifest,
                    out var manifestError,
                    out var manifestErrorKey,
                    out var manifestErrorArgs))
            {
                transaction.Interrupt();
                SaveTransactionState(normalizedRoot, transaction);
                var eventArgs = new Dictionary<string, string>
                {
                    ["error"] = manifestError
                };
                if (manifestErrorArgs is not null)
                {
                    foreach (var item in manifestErrorArgs)
                    {
                        eventArgs[item.Key] = item.Value;
                    }
                }

                progress?.Report(new CreatorStepProgress(
                    CreatorStep.PrepareRelease,
                    CreatorStepStatus.Error,
                    manifestErrorKey ?? "launcher.create.error.release",
                    eventArgs));

                return new CreateUsbResult(
                    false,
                    normalizedRoot,
                    transaction,
                    manifestError,
                    CreatorStep.PrepareRelease,
                    manifestErrorKey,
                    manifestErrorArgs,
                    releaseManifestPath,
                    GetCreateErrorSafetyKey(CreatorStep.PrepareRelease, manifestErrorKey));
            }
        }
        else if (!string.IsNullOrWhiteSpace(releaseManifestPath))
        {
            if (!TryReadReleaseManifest(
                    releaseManifestPath,
                    out manifest,
                    out var manifestError,
                    out var manifestErrorKey,
                    out var manifestErrorArgs))
            {
                transaction.Interrupt();
                SaveTransactionState(normalizedRoot, transaction);
                var eventArgs = new Dictionary<string, string>
                {
                    ["error"] = manifestError
                };
                if (manifestErrorArgs is not null)
                {
                    foreach (var item in manifestErrorArgs)
                    {
                        eventArgs[item.Key] = item.Value;
                    }
                }

                progress?.Report(new CreatorStepProgress(
                    CreatorStep.PrepareRelease,
                    CreatorStepStatus.Error,
                    manifestErrorKey ?? "launcher.create.error.release",
                    eventArgs));

                return new CreateUsbResult(
                    false,
                    normalizedRoot,
                    transaction,
                    manifestError,
                    CreatorStep.PrepareRelease,
                    manifestErrorKey,
                    manifestErrorArgs,
                    releaseManifestPath,
                    GetCreateErrorSafetyKey(CreatorStep.PrepareRelease, manifestErrorKey));
            }
        }

        var releaseImage = Path.GetFullPath(releaseDestinationPath);
        if (!File.Exists(releaseImage))
        {
            transaction.Interrupt();
            SaveTransactionState(normalizedRoot, transaction);
            var message = "Release image is missing and cannot be resumed.";
            progress?.Report(new CreatorStepProgress(
                CreatorStep.PrepareRelease,
                CreatorStepStatus.Error,
                "launcher.create.error.release",
                new Dictionary<string, string>
                {
                    ["error"] = message
                }));
            return new CreateUsbResult(
                false,
                normalizedRoot,
                transaction,
                message,
                CreatorStep.PrepareRelease,
                "launcher.create.error.release",
                null,
                Path.GetFullPath(releaseDestinationPath),
                GetCreateErrorSafetyKey(CreatorStep.PrepareRelease, "launcher.create.error.release"));
        }
        if (!isPreparingRelease)
        {
            releaseImageFromCache = true;
        }

        var downloadResult = new ReleaseDownloadResult(
            true,
            releaseImage,
            null,
            releaseImageFromCache);

        try
        {
            await RunSimulationAsync(transaction, progress, failStep, stepDelayMs, transactionStatePath);
        }
        catch (Exception exception)
        {
            transaction.Interrupt();
            SaveTransactionState(normalizedRoot, transaction);
            progress?.Report(new CreatorStepProgress(
                transaction.Current,
                CreatorStepStatus.Error,
                "launcher.create.error.simulation",
                new Dictionary<string, string>
                {
                    ["error"] = exception.Message
                }));
            return new CreateUsbResult(
                false,
                normalizedRoot,
                transaction,
                exception.Message,
                transaction.Current,
                ErrorSafetyMessageKey: GetCreateErrorSafetyKey(transaction.Current, "launcher.create.error.simulation"));
        }

        var metadata = BuildDeviceMetadata(
            releaseImagePath: downloadResult.FilePath,
            releaseSource: releaseSource,
            releaseSha256: releaseSha256,
            releaseManifest: manifest,
            releaseFromCache: downloadResult.FromCache);

        var metadataPath = Path.Combine(normalizedRoot, DeviceFilePath);
        var startHtmlPath = Path.Combine(normalizedRoot, StartHtmlFile);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            File.WriteAllText(metadataPath, metadata);
            File.WriteAllText(startHtmlPath, StartHtmlTemplate);
            DeleteTransactionState(normalizedRoot);
        }
        catch (Exception exception)
        {
            transaction.Interrupt();
            SaveTransactionState(normalizedRoot, transaction);
            return new CreateUsbResult(
                false,
                normalizedRoot,
                transaction,
                exception.Message,
                CreatorStep.Complete,
                ErrorSafetyMessageKey: GetCreateErrorSafetyKey(CreatorStep.Complete, "launcher.create.error.metadata_write"));
        }

        return new CreateUsbResult(true, normalizedRoot, transaction);
    }

    public static CreateUsbResult RunCreateFlow(
        string usbRootPath,
        IProgress<CreatorStep>? progress,
        int stepDelayMs = 130)
    {
        var fallback = progress is null
            ? null
            : new Progress<CreatorStepProgress>(step => progress.Report(step.Step));
        return RunCreateFlow(usbRootPath, fallback, null, stepDelayMs);
    }

    public static CreateUsbResult RunCreateFlow(
        string usbRootPath,
        IProgress<CreatorStepProgress>? progress,
        Func<CreatorStep, bool>? failStep,
        int stepDelayMs = 130,
        CreatorTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(usbRootPath))
        {
            throw new ArgumentException("USB root path is required.", nameof(usbRootPath));
        }

        var normalizedRoot = Path.GetFullPath(usbRootPath);
        Directory.CreateDirectory(normalizedRoot);
        var transactionStatePath = normalizedRoot;

        transaction ??= CreateTransaction();
        try
        {
            RunSimulation(transaction, progress, failStep, stepDelayMs, transactionStatePath);
        }
        catch (Exception exception)
        {
            transaction.Interrupt();
            SaveTransactionState(normalizedRoot, transaction);
            progress?.Report(new CreatorStepProgress(
                transaction.Current,
                CreatorStepStatus.Error,
                "launcher.create.error.simulation",
                new Dictionary<string, string>
                {
                    ["error"] = exception.Message
                }));
            return new CreateUsbResult(
                false,
                normalizedRoot,
                transaction,
                exception.Message,
                transaction.Current,
                ErrorSafetyMessageKey: GetCreateErrorSafetyKey(transaction.Current, "launcher.create.error.simulation"));
        }

        var metadata = BuildDeviceMetadata();

        var metadataPath = Path.Combine(normalizedRoot, DeviceFilePath);
        var startHtmlPath = Path.Combine(normalizedRoot, StartHtmlFile);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(metadataPath)!);
            File.WriteAllText(metadataPath, metadata);
            File.WriteAllText(startHtmlPath, StartHtmlTemplate);
            DeleteTransactionState(normalizedRoot);
        }
        catch (Exception exception)
        {
            transaction.Interrupt();
            SaveTransactionState(normalizedRoot, transaction);
            return new CreateUsbResult(
                false,
                normalizedRoot,
                transaction,
                exception.Message,
                CreatorStep.Complete,
                ErrorSafetyMessageKey: GetCreateErrorSafetyKey(CreatorStep.Complete, "launcher.create.error.metadata_write"));
        }

        return new CreateUsbResult(true, normalizedRoot, transaction);
    }

    public static CreateUsbResult RunCreateFlow(
        string usbRootPath,
        IProgress<CreatorStepProgress>? progress,
        int stepDelayMs = 130)
    {
        return RunCreateFlow(usbRootPath, progress, null, stepDelayMs);
    }

    public static void RunSimulation(CreatorTransaction transaction)
    {
        RunSimulation(transaction, (IProgress<CreatorStep>?)null);
    }

    public static void RunSimulation(CreatorTransaction transaction, IProgress<CreatorStep>? progress, int stepDelayMs = 130)
    {
        var fallback = progress is null
            ? null
            : new Progress<CreatorStepProgress>(step => progress.Report(step.Step));
        RunSimulation(transaction, fallback, null, stepDelayMs, null);
    }

    public static void RunSimulation(
        CreatorTransaction transaction,
        IProgress<CreatorStepProgress>? progress,
        int stepDelayMs = 130)
    {
        RunSimulation(transaction, progress, null, stepDelayMs, null);
    }

    public static void RunSimulation(
        CreatorTransaction transaction,
        IProgress<CreatorStepProgress>? progress,
        Func<CreatorStep, bool>? failStep,
        int stepDelayMs = 130,
        string? transactionStatePath = null)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var skipWriteSystemImageAfterResume = false;
        if (transaction.IsInterrupted)
        {
            transaction.ResumeFrom(transaction.LastCompleted);
            skipWriteSystemImageAfterResume = transaction.Current == CreatorStep.FormatUsb;
        }

        while (true)
        {
            var current = transaction.Current;
            if (skipWriteSystemImageAfterResume && current == CreatorStep.WriteSystemImage)
            {
                transaction.MarkCompleted(current);
                SaveTransactionStateIfNeeded();
                if (stepDelayMs > 0)
                {
                    Thread.Sleep(stepDelayMs);
                }

                skipWriteSystemImageAfterResume = false;
                continue;
            }

            if (failStep is not null && failStep(current))
            {
                throw new InvalidOperationException("Simulated USB creation failure for testing.");
            }

            var status = current == CreatorStep.Complete
                ? CreatorStepStatus.Success
                : current == CreatorStep.VerifyUsb
                    ? CreatorStepStatus.Warning
                    : CreatorStepStatus.Info;
            var messageKey = status == CreatorStepStatus.Warning
                ? "launcher.create.warning.verify_usb"
                : null;

            progress?.Report(new CreatorStepProgress(current, status, messageKey));

            if (current == CreatorStep.Complete)
            {
                transaction.MarkCompleted(current);
                SaveTransactionStateIfNeeded();
                return;
            }

            transaction.MarkCompleted(current);
            SaveTransactionStateIfNeeded();
            if (stepDelayMs > 0)
            {
                Thread.Sleep(stepDelayMs);
            }
        }

        void SaveTransactionStateIfNeeded()
        {
            if (string.IsNullOrWhiteSpace(transactionStatePath))
            {
                return;
            }

            SaveTransactionState(transactionStatePath, transaction);
        }
    }

    public static Task RunSimulationAsync(
        CreatorTransaction transaction,
        IProgress<CreatorStepProgress>? progress,
        Func<CreatorStep, bool>? failStep,
        int stepDelayMs = 130,
        string? transactionStatePath = null)
    {
        return Task.Run(() => RunSimulation(transaction, progress, failStep, stepDelayMs, transactionStatePath));
    }

    private static string BuildDeviceMetadata(
        string? releaseImagePath = null,
        string? releaseSource = null,
        string? releaseSha256 = null,
        ReleaseManifest? releaseManifest = null,
        bool? releaseFromCache = null)
    {
        var payload = new Dictionary<string, object>
        {
            ["created_utc"] = DateTime.UtcNow.ToString("O"),
            ["state"] = "ready",
            ["tool_version"] = "0.1",
            ["locale"] = CultureInfo.CurrentUICulture.Name
        };

        if (!string.IsNullOrWhiteSpace(releaseImagePath))
        {
            payload["release_image_path"] = Path.GetFullPath(releaseImagePath);
            payload["release_source"] = releaseSource ?? string.Empty;
            payload["release_sha256"] = releaseSha256 ?? string.Empty;
            payload["release_image_from_cache"] = releaseFromCache ?? false;
        }

        if (releaseManifest is not null)
        {
            payload["release_manifest_version"] = releaseManifest.Version;
            payload["release_manifest_channel"] = releaseManifest.Channel;
            payload["release_manifest_slots"] = releaseManifest.Slots.Count;
            payload["release_manifest_artifacts"] = releaseManifest.Artifacts.Count;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static bool IsSha256Valid(string sha)
    {
        if (sha.Length != 64)
        {
            return false;
        }

        foreach (var c in sha)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryResolveReleaseSource(string source, out string resolvedSource)
    {
        resolvedSource = source;

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (uri.IsFile && !string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                if (!TryResolveFileUri(uri, out var filePath))
                {
                    return false;
                }

                resolvedSource = Path.GetFullPath(filePath);
                return true;
            }

            if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        if (!Path.IsPathRooted(source))
        {
            return false;
        }

        if (!File.Exists(source))
        {
            return false;
        }

        resolvedSource = Path.GetFullPath(source);
        return true;
    }

    private static bool TryResolveFileUri(Uri uri, out string normalizedPath)
    {
        normalizedPath = string.Empty;
        if (!uri.IsFile || uri.LocalPath is null)
        {
            return false;
        }

        var localPath = Uri.UnescapeDataString(uri.LocalPath);
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            if (localPath.StartsWith(@"\\localhost\", StringComparison.OrdinalIgnoreCase))
            {
                localPath = localPath["\\\\localhost\\".Length..];
            }
        }
        else if (uri.IsUnc)
        {
            localPath = localPath.Replace('/', '\\');
        }

        normalizedPath = localPath;

        return File.Exists(normalizedPath);
    }

    private static bool TryResolveReleaseManifest(string manifestPath, out string resolvedManifestPath)
    {
        resolvedManifestPath = manifestPath;

        if (Uri.TryCreate(manifestPath, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile || string.IsNullOrWhiteSpace(uri.LocalPath))
            {
                return false;
            }

            if (!TryResolveFileUri(uri, out var resolvedPath) || !TryValidateManifestFile(resolvedPath))
            {
                return false;
            }

            resolvedManifestPath = resolvedPath;
            return true;
        }

        if (!Path.IsPathRooted(manifestPath) || !TryValidateManifestFile(manifestPath))
        {
            return false;
        }

        resolvedManifestPath = Path.GetFullPath(manifestPath);
        return true;
    }

    private static bool TryResolveImplicitManifestPath(string releaseSource, out string manifestPath)
    {
        manifestPath = string.Empty;
        var sourceDir = Path.GetDirectoryName(releaseSource);
        if (string.IsNullOrWhiteSpace(sourceDir))
        {
            return false;
        }

        var candidate = Path.Combine(sourceDir, DefaultReleaseManifestFile);
        if (!File.Exists(candidate))
        {
            return false;
        }

        manifestPath = candidate;
        return true;
    }

    private static bool IsLocalFileUriOrPath(string releaseSource)
    {
        if (Uri.TryCreate(releaseSource, UriKind.Absolute, out var uri))
        {
            return uri.IsFile && string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                ? true
                : uri.IsFile;
        }

        return File.Exists(releaseSource);
    }

    private static bool TryReadReleaseManifest(
        string? manifestPath,
        out ReleaseManifest? releaseManifest,
        out string error,
        out string? errorMessageKey,
        out IReadOnlyDictionary<string, string>? errorMessageArgs)
    {
        releaseManifest = null;
        error = "Release manifest is required.";
        errorMessageKey = null;
        errorMessageArgs = null;

        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            return true;
        }

        try
        {
            var content = File.ReadAllText(manifestPath);
            var parsed = JsonSerializer.Deserialize<ReleaseManifest>(content);
            if (parsed is null)
            {
                error = "Invalid release manifest format.";
                errorMessageKey = "launcher.create.error.release_manifest_invalid";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["error"] = error
                };
                return false;
            }

            return TryValidateReleaseManifest(
                parsed,
                out error,
                out errorMessageKey,
                out errorMessageArgs,
                out releaseManifest);
        }
        catch (Exception exception)
        {
            error = exception.Message;
            errorMessageArgs = new Dictionary<string, string>
            {
                ["error"] = error
            };
            return false;
        }
    }

    private static bool TryValidateReleaseManifest(
        ReleaseManifest releaseManifest,
        out string error,
        out string? errorMessageKey,
        out IReadOnlyDictionary<string, string>? errorMessageArgs,
        out ReleaseManifest? validManifest)
    {
        validManifest = null;
        errorMessageKey = null;
        errorMessageArgs = null;
        if (string.IsNullOrWhiteSpace(releaseManifest.Version))
        {
            error = "Release manifest version is missing.";
            errorMessageKey = "launcher.create.error.release_manifest_version_missing";
            errorMessageArgs = new Dictionary<string, string>
            {
                ["error"] = error
            };
            return false;
        }

        if (string.IsNullOrWhiteSpace(releaseManifest.Channel))
        {
            error = "Release manifest channel is missing.";
            errorMessageKey = "launcher.create.error.release_manifest_channel_missing";
            errorMessageArgs = new Dictionary<string, string>
            {
                ["error"] = error
            };
            return false;
        }

        if (releaseManifest.Slots.Count == 0)
        {
            error = "Release manifest contains no slots.";
            errorMessageKey = "launcher.create.error.release_manifest_no_slots";
            errorMessageArgs = new Dictionary<string, string>
            {
                ["error"] = error
            };
            return false;
        }

        var slotNames = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var slot in releaseManifest.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Name))
            {
                error = "Release manifest slot name is missing.";
                errorMessageKey = "launcher.create.error.release_manifest_slot_name_missing";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["error"] = error
                };
                return false;
            }

            if (!slotNames.Add(slot.Name.Trim()))
            {
                error = $"Release manifest slot {slot.Name} is duplicated.";
                errorMessageKey = "launcher.create.error.release_manifest_slot_duplicated";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["slot"] = slot.Name.Trim()
                };
                return false;
            }

            if (string.IsNullOrWhiteSpace(slot.ImagePath))
            {
                error = $"Release manifest slot {slot.Name} image path is missing.";
                errorMessageKey = "launcher.create.error.release_manifest_slot_image_path_missing";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["slot"] = slot.Name.Trim()
                };
                return false;
            }

            if (string.IsNullOrWhiteSpace(slot.Hash))
            {
                error = $"Release manifest slot {slot.Name} hash is missing.";
                errorMessageKey = "launcher.create.error.release_manifest_slot_hash_missing";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["slot"] = slot.Name.Trim()
                };
                return false;
            }

        }

        foreach (var artifact in releaseManifest.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Url))
            {
                error = "Release manifest artifact url is missing.";
                errorMessageKey = "launcher.create.error.release_manifest_artifact_url_missing";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["error"] = error,
                    ["file"] = artifact.FileName ?? "unknown"
                };
                return false;
            }

            if (!Uri.TryCreate(artifact.Url, UriKind.Absolute, out var artifactUri)
                || (!artifactUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !artifactUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Release manifest artifact {artifact.Url} url is invalid.";
                errorMessageKey = "launcher.create.error.release_manifest_artifact_url_invalid";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["url"] = artifact.Url
                };
                return false;
            }

            if (string.IsNullOrWhiteSpace(artifact.FileName))
            {
                error = "Release manifest artifact file name is missing.";
                errorMessageKey = "launcher.create.error.release_manifest_artifact_file_name_missing";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["error"] = error
                };
                return false;
            }

            if (string.IsNullOrWhiteSpace(artifact.Sha256))
            {
                error = $"Release manifest artifact {artifact.FileName} hash is missing.";
                errorMessageKey = "launcher.create.error.release_manifest_artifact_hash_missing";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["file"] = artifact.FileName
                };
                return false;
            }

            if (!IsSha256Valid(artifact.Sha256))
            {
                error = $"Release manifest artifact {artifact.FileName} hash is invalid.";
                errorMessageKey = "launcher.create.error.release_manifest_artifact_hash_invalid";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["file"] = artifact.FileName
                };
                return false;
            }
        }

        foreach (var slot in releaseManifest.Slots)
        {
            if (!IsSha256Valid(slot.Hash))
            {
                error = $"Release manifest slot {slot.Name} hash is invalid.";
                errorMessageKey = "launcher.create.error.release_manifest_slot_hash_invalid";
                errorMessageArgs = new Dictionary<string, string>
                {
                    ["slot"] = slot.Name.Trim()
                };
                return false;
            }
        }

        if (releaseManifest.Artifacts.Count == 0)
        {
            error = "Release manifest contains no artifacts.";
            errorMessageKey = "launcher.create.error.release_manifest_no_artifacts";
            errorMessageArgs = new Dictionary<string, string>
            {
                ["error"] = error
            };
            return false;
        }

        error = string.Empty;
        validManifest = releaseManifest;
        return true;
    }

    private static bool TryValidateManifestFile(string manifestPath)
    {
        return File.Exists(manifestPath);
    }

    private static bool TryResolveReleaseCache(string cacheRoot, out string resolvedCacheRoot)
    {
        resolvedCacheRoot = cacheRoot;

        if (!Path.IsPathRooted(cacheRoot))
        {
            return false;
        }

        try
        {
            resolvedCacheRoot = Path.GetFullPath(cacheRoot);

            Directory.CreateDirectory(resolvedCacheRoot);
            var writeProbe = Path.Combine(resolvedCacheRoot, $".dextor-release-cache-{Guid.NewGuid():N}");
            try
            {
                File.WriteAllText(writeProbe, string.Empty);
            }
            finally
            {
                if (File.Exists(writeProbe))
                {
                    File.Delete(writeProbe);
                }
            }
        }
        catch (Exception)
        {
            return false;
        }

        return true;
    }
}
