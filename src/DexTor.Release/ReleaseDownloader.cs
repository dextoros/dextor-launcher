using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DexTor.Release;

public sealed record ReleaseDownloadResult(
    bool Success,
    string FilePath,
    string? ErrorMessage = null,
    bool FromCache = false);

public interface IReleaseDownloader
{
    Task<ReleaseDownloadResult> DownloadAsync(
        string source,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken = default);
}

public sealed class ReleaseDownloader : IReleaseDownloader
{
    private readonly HttpClient _httpClient;

    public ReleaseDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<ReleaseDownloadResult> DownloadAsync(
        string source,
        string destinationPath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new ReleaseDownloadResult(false, destinationPath, "Source is required.");
        }

        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            return new ReleaseDownloadResult(false, destinationPath, "Expected SHA-256 hash is required.");
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return new ReleaseDownloadResult(false, destinationPath, "Destination path is required.");
        }

        try
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            if (File.Exists(destinationPath) && await VerifyAsync(destinationPath, expectedSha256, cancellationToken))
            {
                return new ReleaseDownloadResult(true, destinationPath, FromCache: true);
            }

            var tempPath = destinationPath + ".tmp";
            try
            {
                if (TryGetLocalPath(source, out var localPath) && !string.IsNullOrWhiteSpace(localPath))
                {
                    await CopyLocalAsync(localPath, tempPath, cancellationToken);
                }
                else
                {
                    await DownloadFromHttpAsync(source, tempPath, cancellationToken);
                }

                if (!await VerifyAsync(tempPath, expectedSha256, cancellationToken))
                {
                    return new ReleaseDownloadResult(
                        false,
                        destinationPath,
                        "SHA-256 verification failed.");
                }

                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                File.Move(tempPath, destinationPath);
                return new ReleaseDownloadResult(true, destinationPath);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
        }
        catch (Exception exception)
        {
            return new ReleaseDownloadResult(false, destinationPath, exception.Message);
        }
    }

    private static bool TryGetLocalPath(string source, out string? localPath)
    {
        if (File.Exists(source))
        {
            localPath = Path.GetFullPath(source);
            return true;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && uri.IsFile && uri.LocalPath is not null
            && TryResolveFilePathFromUri(uri, out var uriLocalPath))
        {
            localPath = uriLocalPath;
            return true;
        }

        if (Path.IsPathRooted(source))
        {
            localPath = source;
            return true;
        }

        localPath = null;
        return false;
    }

    private static bool TryResolveFilePathFromUri(Uri uri, out string localPath)
    {
        localPath = string.Empty;
        if (!uri.IsFile || uri.LocalPath is null)
        {
            return false;
        }

        var normalized = Uri.UnescapeDataString(uri.LocalPath);
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            if (normalized.StartsWith(@"\\localhost\", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized["\\\\localhost\\".Length..];
            }
        }
        else if (uri.IsUnc)
        {
            normalized = normalized.Replace('/', '\\');
        }

        localPath = normalized;
        return true;
    }

    private static async Task CopyLocalAsync(string sourcePath, string tempPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Release source file was not found.", sourcePath);
        }

        await using var source = File.OpenRead(sourcePath);
        await using var target = File.Create(tempPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    private async Task DownloadFromHttpAsync(string url, string tempPath, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Failed to download release from {url}.");
        }

        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(tempPath);
        await sourceStream.CopyToAsync(target, cancellationToken);
    }

    private static async Task<bool> VerifyAsync(string filePath, string expectedSha256, CancellationToken cancellationToken)
    {
        var data = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var actualHash = ReleaseIntegrity.ComputeSha256(data);
        var expected = NormalizeHex(expectedSha256);
        var actual = NormalizeHex(actualHash);
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeHex(string value)
    {
        return (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.InvariantCulture)
            .ToUpperInvariant();
    }
}
