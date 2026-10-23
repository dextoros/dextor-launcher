using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>The Store package brings QEMU with it and never downloads it.</summary>
public sealed class QemuRuntimePackagedTests
{
    [Fact]
    public async Task PackagedLauncherWithoutBundledRuntimeReportsItAndNeverDownloads()
    {
        var cacheRoot = CreateTempDirectory();
        var bundledRoot = CreateTempDirectory();
        try
        {
            var downloadWasRequested = false;
            var installerWasStarted = false;
            using var client = new HttpClient(new StubHttpHandler(_ =>
            {
                downloadWasRequested = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }));
            var service = new QemuRuntimePreparationService(
                client,
                (_, _, _) =>
                {
                    installerWasStarted = true;
                    return Task.FromResult(0);
                },
                "https://example.test/qemu.exe",
                new string('a', 128),
                bundledRoot)
            {
                IsPackaged = () => true
            };

            var result = await service.PrepareAsync(cacheRoot, null, CancellationToken.None);

            result.IsReady.Should().BeFalse();
            result.ErrorMessage.Should().Be(QemuRuntimePreparationService.PackagedRuntimeMissingMessage);
            result.RuntimePath.Should().Be(bundledRoot);
            downloadWasRequested.Should().BeFalse("the Store rejects apps that download executable code");
            installerWasStarted.Should().BeFalse();
            Directory.EnumerateFileSystemEntries(cacheRoot).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(bundledRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PackagedLauncherUsesTheRuntimeItShipsWith()
    {
        var cacheRoot = CreateTempDirectory();
        var bundledRoot = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(bundledRoot, "share"));
            File.WriteAllBytes(Path.Combine(bundledRoot, "qemu-system-x86_64.exe"), []);
            File.WriteAllText(
                Path.Combine(bundledRoot, "qemu-runtime-files.json"),
                JsonSerializer.Serialize(new { schemaVersion = 1, version = QemuRuntimePreparationService.Version }));
            var service = new QemuRuntimePreparationService(bundledRuntimePath: bundledRoot) { IsPackaged = () => true };

            var result = await service.PrepareAsync(cacheRoot, null, CancellationToken.None);

            result.IsReady.Should().BeTrue(result.ErrorMessage);
            result.RuntimePath.Should().Be(bundledRoot);
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(bundledRoot, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DexTorTests", "PackagedRuntime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
