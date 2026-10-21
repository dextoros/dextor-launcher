using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DexTor.Release;

namespace DexTor.Launcher;

/// <summary>Where the image, its SHA-256 and its signature are published.</summary>
public sealed record LiveImageSourceInfo(
    string SourceUrl,
    string Sha256Url,
    string SignatureUrl,
    string SourceFileName);

public sealed record LiveImagePreparationProgress(
    long BytesDownloaded,
    long? TotalBytes,
    double? BytesPerSecond,
    TimeSpan? EstimatedRemaining,
    string Stage);

/// <param name="SigningKey">The key the image was found signed with, "DexTor OS Release · E246 B0C5 ...".</param>
public sealed record LiveImagePreparationResult(
    bool Success,
    string? SourceUrl,
    string? LocalImagePath,
    string? SourceSha256,
    long SourceSizeBytes = 0,
    string? ErrorMessage = null,
    string? ErrorMessageKey = null,
    IReadOnlyDictionary<string, string>? ErrorMessageArgs = null,
    string? SigningKey = null);

public sealed record LiveImageCacheInspectionResult(
    bool Success,
    bool IsReady,
    string CacheRoot,
    string? SourceUrl,
    string? LocalImagePath,
    string? SourceSha256,
    long SourceSizeBytes = 0,
    string? ErrorMessage = null,
    string? ErrorMessageKey = null,
    string? SigningKey = null);

/// <summary>
/// Fetches the current DexTor OS image into the cache and proves it is the one the release key
/// signed.
/// </summary>
/// <remarks>
/// Next to the image the server publishes <c>.sha256</c> and <c>.sig</c>. The signature is
/// checked against the published SHA-256 before anything is downloaded - a release whose
/// signature does not hold is not worth 2 GB of the operator's connection - and the image
/// itself must then hash to exactly that SHA-256. Only MD5 used to be checked, which proves
/// nothing about who made the file.
/// </remarks>
public sealed class LiveImagePreparationService
{
    public const string LiveImageSourceIndexUrl = "https://dextoros.com/public/lastversion.link";
    public const string StageResolveIndex = "launcher.create.live_image.prepare.status.resolve";
    public const string StageResolveIntegrity = "launcher.create.live_image.prepare.status.resolve_integrity";
    public const string StageVerifyCache = "launcher.create.live_image.prepare.status.verify_cache";
    public const string StageDownload = "launcher.create.live_image.prepare.status.downloading";
    public const string StageResume = "launcher.create.live_image.prepare.status.resuming";
    public const string StageVerifySha256 = "launcher.create.live_image.prepare.status.verify_sha256";
    private const string FailureMessageKey = "launcher.create.live_image.prepare.failed";

    /// <summary>The image could not be fetched: no connection, the server refused, the download broke off.</summary>
    public const string DownloadFailedKey = "launcher.create.live_image.prepare.failed_download";

    /// <summary>The image arrived but does not match the published SHA-256; it was deleted.</summary>
    public const string ChecksumFailedKey = "launcher.create.live_image.prepare.failed_checksum";

    /// <summary>
    /// The release key's signature is missing or does not match the published SHA-256; any
    /// cached copy of the image was deleted.
    /// </summary>
    public const string SignatureFailedKey = "launcher.create.live_image.prepare.failed_signature";

    /// <summary>The disk holding the cache has no room for the image ({drive}, {needed}, {available}).</summary>
    public const string NoSpaceKey = "launcher.create.live_image.prepare.failed_space";

    private readonly HttpClient _httpClient;
    private readonly ImageSigningKey _signingKey;

    public LiveImagePreparationService(HttpClient? httpClient = null, ImageSigningKey? signingKey = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _signingKey = signingKey ?? ImageSigningKey.Release;
    }

    /// <summary>"DexTor OS Release · E246 B0C5 ...", for the details on step 3.</summary>
    public string SigningKeyDescription => $"{_signingKey.Name} · {_signingKey.Fingerprint}";

    public async Task<LiveImageCacheInspectionResult> InspectCacheFromIndexAsync(
        string cacheRoot,
        IProgress<LiveImagePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cacheRoot))
            {
                return CacheInspectionFailure(cacheRoot, "Cache root is required.");
            }

            EnsureCacheIsWritable(cacheRoot);

            progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageResolveIndex));
            var sourceInfo = await ResolveSourceInfoAsync(LiveImageSourceIndexUrl, cancellationToken);
            if (sourceInfo is null)
            {
                return CacheInspectionFailure(cacheRoot, "Could not resolve ISO source URL.");
            }

            progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageResolveIntegrity));
            var integrity = await ResolveIntegrityAsync(sourceInfo, cancellationToken);
            var destinationPath = Path.Combine(cacheRoot, sourceInfo.SourceFileName);
            if (integrity.FailureKey is { } failureKey)
            {
                if (failureKey == SignatureFailedKey) DiscardImage(destinationPath);
                return CacheInspectionFailure(cacheRoot, integrity.Message!, sourceInfo.SourceUrl, failureKey);
            }

            // Earlier versions are superseded as soon as the current one is known.
            TemporaryCacheCleaner.RemoveOtherImages(cacheRoot, sourceInfo.SourceFileName);
            if (!File.Exists(destinationPath))
            {
                return new LiveImageCacheInspectionResult(
                    true,
                    false,
                    cacheRoot,
                    sourceInfo.SourceUrl,
                    destinationPath,
                    null);
            }

            var cachedSha256 = await ComputeSha256Async(destinationPath, progress, StageVerifyCache, cancellationToken);
            if (!string.Equals(cachedSha256, integrity.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new LiveImageCacheInspectionResult(
                    true,
                    false,
                    cacheRoot,
                    sourceInfo.SourceUrl,
                    destinationPath,
                    null,
                    new FileInfo(destinationPath).Length);
            }

            return new LiveImageCacheInspectionResult(
                true,
                true,
                cacheRoot,
                sourceInfo.SourceUrl,
                destinationPath,
                cachedSha256,
                new FileInfo(destinationPath).Length,
                SigningKey: SigningKeyDescription);
        }
        catch (TaskCanceledException)
        {
            return CacheInspectionFailure(cacheRoot, "ISO cache inspection was cancelled.");
        }
        catch (Exception exception)
        {
            return CacheInspectionFailure(cacheRoot, exception.Message);
        }
    }

    public async Task<LiveImagePreparationResult> PrepareFromIndexAsync(
        string cacheRoot,
        IProgress<LiveImagePreparationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cacheRoot))
            {
                return new LiveImagePreparationResult(
                    false,
                    null,
                    null,
                    null,
                    0,
                    "Cache root is required.",
                    FailureMessageKey);
            }

            Directory.CreateDirectory(cacheRoot);

            progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageResolveIndex));
            var sourceInfo = await ResolveSourceInfoAsync(LiveImageSourceIndexUrl, cancellationToken);
            if (sourceInfo is null)
            {
                return new LiveImagePreparationResult(
                    false,
                    null,
                    null,
                    null,
                    0,
                    "Could not resolve ISO source URL.",
                    DownloadFailedKey);
            }

            progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageResolveIntegrity));
            var integrity = await ResolveIntegrityAsync(sourceInfo, cancellationToken);
            var destinationPath = Path.Combine(cacheRoot, sourceInfo.SourceFileName);
            if (integrity.FailureKey is { } failureKey)
            {
                if (failureKey == SignatureFailedKey) DiscardImage(destinationPath);
                return new LiveImagePreparationResult(
                    false,
                    sourceInfo.SourceUrl,
                    null,
                    null,
                    0,
                    integrity.Message,
                    failureKey);
            }

            TemporaryCacheCleaner.RemoveOtherImages(cacheRoot, sourceInfo.SourceFileName);
            if (await TryUseCachedImageAsync(destinationPath, integrity.Sha256!, progress, cancellationToken) is { } cachedSourceSha256)
            {
                return new LiveImagePreparationResult(
                    true,
                    sourceInfo.SourceUrl,
                    destinationPath,
                    cachedSourceSha256,
                    new FileInfo(destinationPath).Length,
                    SigningKey: SigningKeyDescription);
            }

            progress?.Report(new LiveImagePreparationProgress(0, null, null, null, StageDownload));
            var download = await DownloadWithProgressAsync(sourceInfo.SourceUrl, destinationPath, progress, cancellationToken);
            if (download.NoSpace is { } space)
            {
                return new LiveImagePreparationResult(
                    false,
                    sourceInfo.SourceUrl,
                    null,
                    null,
                    0,
                    "Not enough free space for the ISO image.",
                    NoSpaceKey,
                    space);
            }

            if (!download.Success)
            {
                return new LiveImagePreparationResult(
                    false,
                    sourceInfo.SourceUrl,
                    null,
                    null,
                    0,
                    "Could not download ISO image.",
                    DownloadFailedKey);
            }

            var sourceSha256 = await ComputeSha256Async(destinationPath, progress, StageVerifySha256, cancellationToken);
            if (!string.Equals(sourceSha256, integrity.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(destinationPath);
                return new LiveImagePreparationResult(
                    false,
                    sourceInfo.SourceUrl,
                    null,
                    null,
                    0,
                    "ISO SHA-256 does not match the signed checksum.",
                    ChecksumFailedKey);
            }

            return new LiveImagePreparationResult(
                true,
                sourceInfo.SourceUrl,
                destinationPath,
                sourceSha256,
                new FileInfo(destinationPath).Length,
                SigningKey: SigningKeyDescription);
        }
        catch (TaskCanceledException)
        {
            return new LiveImagePreparationResult(
                false,
                null,
                null,
                null,
                0,
                "ISO preparation was cancelled.",
                FailureMessageKey);
        }
        catch (Exception exception)
        {
            return new LiveImagePreparationResult(
                false,
                null,
                null,
                null,
                0,
                exception.Message,
                FailureMessageKey);
        }
    }

    public static LiveImageSourceInfo? ParseSourceInfo(string indexText)
    {
        var line = indexText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        if (!Uri.TryCreate(line, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var fileName = Path.GetFileName(uri.LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var path = uri.GetLeftPart(UriPartial.Path);
        return new LiveImageSourceInfo(
            uri.ToString(),
            $"{path}.sha256",
            $"{path}.sig",
            fileName);
    }

    /// <summary>The first 64-digit hex token of a <c>sha256sum</c>-style file, lower-cased.</summary>
    public static string? ParseSha256FromText(string sha256Text)
    {
        if (string.IsNullOrWhiteSpace(sha256Text))
        {
            return null;
        }

        foreach (var token in sha256Text.Split([' ', '\t', '\r', '\n', '*'], StringSplitOptions.RemoveEmptyEntries))
        {
            var normalized = token.Trim();
            if (normalized.Length == 64 && normalized.All(Uri.IsHexDigit))
            {
                return normalized.ToLowerInvariant();
            }
        }

        return null;
    }

    private static void EnsureCacheIsWritable(string cacheRoot)
    {
        Directory.CreateDirectory(cacheRoot);
        var probePath = Path.Combine(cacheRoot, $".write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(0);
        }
        finally
        {
            TryDelete(probePath);
        }
    }

    private static LiveImageCacheInspectionResult CacheInspectionFailure(
        string? cacheRoot,
        string message,
        string? sourceUrl = null,
        string? messageKey = null)
    {
        return new LiveImageCacheInspectionResult(
            false,
            false,
            cacheRoot ?? string.Empty,
            sourceUrl,
            null,
            null,
            0,
            message,
            messageKey ?? FailureMessageKey);
    }

    private async Task<LiveImageSourceInfo?> ResolveSourceInfoAsync(string indexUrl, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(indexUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var indexText = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseSourceInfo(indexText);
    }

    /// <summary>The published SHA-256 once the release key's signature over it holds, or why not.</summary>
    private sealed record PublishedIntegrity(string? Sha256, string? FailureKey, string? Message);

    private async Task<PublishedIntegrity> ResolveIntegrityAsync(LiveImageSourceInfo source, CancellationToken cancellationToken)
    {
        string? sha256;
        using (var response = await _httpClient.GetAsync(source.Sha256Url, cancellationToken))
        {
            sha256 = response.IsSuccessStatusCode
                ? ParseSha256FromText(await response.Content.ReadAsStringAsync(cancellationToken))
                : null;
        }

        if (sha256 is null)
        {
            return new PublishedIntegrity(null, DownloadFailedKey, "Could not resolve the ISO SHA-256.");
        }

        byte[] signature;
        using (var response = await _httpClient.GetAsync(source.SignatureUrl, cancellationToken))
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new PublishedIntegrity(null, SignatureFailedKey, "The ISO signature is not published.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new PublishedIntegrity(null, DownloadFailedKey, "Could not fetch the ISO signature.");
            }

            signature = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }

        return _signingKey.Verify(sha256, signature)
            ? new PublishedIntegrity(sha256, null, null)
            : new PublishedIntegrity(null, SignatureFailedKey, "The ISO signature does not match its SHA-256 and the release key.");
    }

    /// <summary>A cached image of a release whose signature failed is not kept, nor its partial download.</summary>
    private static void DiscardImage(string destinationPath)
    {
        TryDelete(destinationPath);
        TryDelete(PartialPathFor(destinationPath));
        TryDelete(PartialSourcePathFor(destinationPath));
    }

    /// <summary>Returns the cached image's SHA-256 when it is the signed one, otherwise null.</summary>
    private static async Task<string?> TryUseCachedImageAsync(
        string destinationPath,
        string expectedSha256,
        IProgress<LiveImagePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(destinationPath))
        {
            return null;
        }

        var fileSha256 = await ComputeSha256Async(destinationPath, progress, StageVerifyCache, cancellationToken);
        if (!string.Equals(fileSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(destinationPath);
            return null;
        }

        return fileSha256;
    }

    /// <summary>How much of a partial file is fetched again when a download resumes.</summary>
    internal const long ResumeOverlapBytes = 1024 * 1024;

    /// <summary>The partial download of <paramref name="destinationPath"/>.</summary>
    internal static string PartialPathFor(string destinationPath) => destinationPath + ".tmp";

    /// <summary>Where the partial download remembers which server file it belongs to.</summary>
    internal static string PartialSourcePathFor(string destinationPath) => PartialPathFor(destinationPath) + ".source";

    /// <summary>
    /// Downloads the image, continuing a partial file instead of starting over.
    /// </summary>
    /// <remarks>
    /// The partial file survives a cancel, a dropped connection and a killed launcher, and
    /// the next attempt asks the server only for what is missing. The last megabyte is
    /// fetched again, because a process that died mid-write may have left a torn tail.
    /// The request carries <c>If-Range</c> with the validator the partial file was started
    /// with: if the file on the server has changed since, the server sends the whole new
    /// file and the partial one is discarded, so two versions are never spliced. The
    /// whole-file SHA-256 check that follows still decides whether the result is kept.
    /// </remarks>
    /// <summary>How a download ended: done, failed, or stopped for want of room - with the figures.</summary>
    private sealed record DownloadOutcome(bool Success, IReadOnlyDictionary<string, string>? NoSpace = null)
    {
        internal static readonly DownloadOutcome Done = new(true);
        internal static readonly DownloadOutcome Failed = new(false);
    }

    /// <summary>
    /// "Not enough room" in the terms the wizard shows: the drive, what the image needs and what
    /// is free, in bytes. Null when the drive cannot be read or there is enough room.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? CheckSpace(string path, long neededBytes, Func<string, long?>? freeSpace = null)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root) || neededBytes <= 0) return null;
        long? available;
        try
        {
            available = freeSpace is null ? new DriveInfo(root).AvailableFreeSpace : freeSpace(root);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        // A little headroom: the file system needs some for itself.
        return available is { } free && free < neededBytes + SpaceHeadroomBytes
            ? SpaceArgs(root, neededBytes, free)
            : null;
    }

    private const long SpaceHeadroomBytes = 64L * 1024 * 1024;

    private static Dictionary<string, string> SpaceArgs(string root, long needed, long available) => new()
    {
        ["drive"] = root.TrimEnd('\\'),
        ["needed_bytes"] = needed.ToString(CultureInfo.InvariantCulture),
        ["available_bytes"] = Math.Max(0, available).ToString(CultureInfo.InvariantCulture)
    };

    private static bool IsDiskFull(IOException exception) => (exception.HResult & 0xFFFF) is 112 or 39;

    private async Task<DownloadOutcome> DownloadWithProgressAsync(
        string sourceUrl,
        string destinationPath,
        IProgress<LiveImagePreparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = PartialPathFor(destinationPath);
        var sourcePath = PartialSourcePathFor(destinationPath);
        try
        {
            var resumeFrom = 0L;
            var validator = ReadPartialValidator(sourcePath, sourceUrl);
            if (File.Exists(tempPath) && validator is not null)
            {
                resumeFrom = Math.Max(0, new FileInfo(tempPath).Length - ResumeOverlapBytes);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            if (resumeFrom > 0)
            {
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
                request.Headers.TryAddWithoutValidation("If-Range", validator);
                progress?.Report(new LiveImagePreparationProgress(resumeFrom, null, null, null, StageResume));
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // The partial file no longer fits the server's file; start clean next time.
                DiscardPartial(tempPath, sourcePath);
                return DownloadOutcome.Failed;
            }

            if (!response.IsSuccessStatusCode)
            {
                return DownloadOutcome.Failed;
            }

            var resuming = response.StatusCode == HttpStatusCode.PartialContent
                && response.Content.Headers.ContentRange is { From: { } from }
                && from == resumeFrom;
            if (!resuming)
            {
                resumeFrom = 0;
                WritePartialValidator(sourcePath, sourceUrl, ValidatorOf(response));
            }

            long? totalBytes = resuming
                ? response.Content.Headers.ContentRange!.Length
                    ?? (response.Content.Headers.ContentLength is { } rest ? resumeFrom + rest : null)
                : response.Content.Headers.ContentLength;
            if (totalBytes is { } size && CheckSpace(tempPath, size - resumeFrom) is { } noRoom)
            {
                return new DownloadOutcome(false, noRoom);
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            var started = DateTime.UtcNow;
            var lastProgressAt = started;
            long downloaded = resumeFrom;
            // A resumed download starts where it is, not at zero.
            progress?.Report(new LiveImagePreparationProgress(downloaded, totalBytes, null, null, StageDownload));
            var buffer = new byte[1024 * 64];
            await using (var destination = new FileStream(tempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
            {
                destination.SetLength(resumeFrom);
                destination.Position = resumeFrom;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    downloaded += read;

                    var now = DateTime.UtcNow;
                    var isComplete = totalBytes is { } total && downloaded >= total;
                    if (now - lastProgressAt < TimeSpan.FromMilliseconds(100) && !isComplete)
                    {
                        continue;
                    }

                    // Speed is what this session moved, not what an earlier one already had.
                    var elapsed = now - started;
                    var speed = elapsed.TotalSeconds > 0 ? (downloaded - resumeFrom) / elapsed.TotalSeconds : (double?)null;
                    TimeSpan? eta = null;
                    if (totalBytes is { } knownTotal && speed is { } safeSpeed && safeSpeed > 0)
                    {
                        var remaining = Math.Max(knownTotal - downloaded, 0);
                        eta = TimeSpan.FromSeconds(remaining / safeSpeed);
                    }

                    progress?.Report(new LiveImagePreparationProgress(downloaded, totalBytes, speed, eta, StageDownload));
                    lastProgressAt = now;
                }

                await destination.FlushAsync(cancellationToken);
            }

            if (totalBytes is { } expected && downloaded != expected)
            {
                // The connection ended early. What arrived is kept for the next attempt.
                return DownloadOutcome.Failed;
            }

            TryDelete(destinationPath);
            File.Move(tempPath, destinationPath);
            TryDelete(sourcePath);
            return DownloadOutcome.Done;
        }
        catch (IOException exception) when (IsDiskFull(exception))
        {
            var root = Path.GetPathRoot(Path.GetFullPath(tempPath)) ?? string.Empty;
            long free = 0;
            try { free = new DriveInfo(root).AvailableFreeSpace; } catch (Exception inner) when (inner is IOException or ArgumentException or UnauthorizedAccessException) { }
            return new DownloadOutcome(false, SpaceArgs(root, Math.Max(free + 1, 0), free));
        }
        catch
        {
            // Cancelled, disconnected or failed mid-way: the partial file stays so the
            // next attempt continues from here.
            return DownloadOutcome.Failed;
        }
    }

    private static string? ValidatorOf(HttpResponseMessage response) =>
        response.Headers.ETag is { IsWeak: false } etag
            ? etag.Tag
            : response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The validator a partial file was started with, if it belongs to this URL.</summary>
    private static string? ReadPartialValidator(string sourcePath, string sourceUrl)
    {
        try
        {
            if (!File.Exists(sourcePath)) return null;
            var lines = File.ReadAllLines(sourcePath);
            return lines.Length >= 2
                && string.Equals(lines[0], sourceUrl, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(lines[1])
                    ? lines[1]
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void WritePartialValidator(string sourcePath, string sourceUrl, string? validator)
    {
        // Without a validator the server's file cannot be told apart from a newer one, so
        // such a partial file is never resumed.
        if (string.IsNullOrWhiteSpace(validator))
        {
            TryDelete(sourcePath);
            return;
        }

        File.WriteAllLines(sourcePath, [sourceUrl, validator]);
    }

    private static void DiscardPartial(string tempPath, string sourcePath)
    {
        TryDelete(tempPath);
        TryDelete(sourcePath);
    }
    /// <summary>
    /// Reads the image once for its SHA-256, reporting how far it has got.
    /// </summary>
    /// <remarks>
    /// A 2 GB image read with nothing on screen but a sweeping bar looked stuck; the byte
    /// count shows it is moving.
    /// </remarks>
    internal static Task<string> ComputeSha256Async(
        string filePath,
        IProgress<LiveImagePreparationProgress>? progress,
        string stage,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var total = stream.Length;
            var buffer = new byte[4 * 1024 * 1024];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var lastReport = TimeSpan.FromSeconds(-1);
            long processed = 0;
            progress?.Report(new LiveImagePreparationProgress(0, total, null, null, stage));
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                sha256.AppendData(buffer, 0, read);
                processed += read;
                var now = clock.Elapsed;
                if (now - lastReport < TimeSpan.FromMilliseconds(100) && processed < total) continue;
                lastReport = now;
                var speed = now.TotalSeconds > 0 ? processed / now.TotalSeconds : (double?)null;
                TimeSpan? eta = speed is > 0 ? TimeSpan.FromSeconds(Math.Max(0, total - processed) / speed.Value) : null;
                progress?.Report(new LiveImagePreparationProgress(processed, total, speed, eta, stage));
            }

            return Convert.ToHexStringLower(sha256.GetHashAndReset());
        }, cancellationToken);
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
        catch
        {
            // Ignore cleanup errors.
        }
    }
}
