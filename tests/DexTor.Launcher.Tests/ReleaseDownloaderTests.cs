using System.IO;
using System.Threading.Tasks;

using FluentAssertions;

using DexTor.Release;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class ReleaseDownloaderTests
{
    [Fact]
    public async Task DownloadAsyncDeletesTempFileOnMissingLocalSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor_release_downloader_tests", Path.GetRandomFileName());
        var source = Path.Combine(root, "missing_system.img");
        var destination = Path.Combine(root, "cache", "system.img");
        var destinationTemp = destination + ".tmp";

        try
        {
            var downloader = new ReleaseDownloader();
            var result = await downloader.DownloadAsync(
                source,
                destination,
                new string('a', 64));

            result.Success.Should().BeFalse();
            File.Exists(destinationTemp).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsyncDeletesTempFileOnHashMismatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor_release_downloader_tests", Path.GetRandomFileName());
        var source = Path.Combine(root, "system.img");
        var destination = Path.Combine(root, "cache", "system.img");
        var destinationTemp = destination + ".tmp";

        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(source, "dextor-release-payload");

            var downloader = new ReleaseDownloader();
            var result = await downloader.DownloadAsync(
                source,
                destination,
                new string('f', 64));

            result.Success.Should().BeFalse();
            File.Exists(destinationTemp).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task DownloadAsyncResolvesLocalhostFileUriSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor_release_downloader_tests", Path.GetRandomFileName());
        var source = Path.Combine(root, "system image.img");
        var destination = Path.Combine(root, "cache", "system.img");
        var payload = "dextor-release-payload";
        var encodedPath = Path.GetFullPath(source)
            .Replace("\\", "/", StringComparison.InvariantCulture)
            .Replace(":", "%3A");
        var sourceUri = $"file://localhost/{encodedPath}";

        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(source, payload);

            var expectedSha = ReleaseIntegrity.ComputeSha256(System.Text.Encoding.UTF8.GetBytes(payload));
            var downloader = new ReleaseDownloader();

            var result = await downloader.DownloadAsync(
                sourceUri,
                destination,
                expectedSha);

            result.Success.Should().BeTrue();
            File.Exists(result.FilePath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
