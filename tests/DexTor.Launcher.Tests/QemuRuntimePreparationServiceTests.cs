using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class QemuRuntimePreparationServiceTests
{
    [Fact]
    public async Task PrepareDownloadsVerifiesAndExtractsRuntimeIntoVersionedCache()
    {
        var root = CreateTempDirectory();
        var installer = "test-qemu-installer"u8.ToArray();
        var expectedHash = Convert.ToHexString(SHA512.HashData(installer)).ToLowerInvariant();
        try
        {
            using var client = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installer)
            }));
            var service = new QemuRuntimePreparationService(
                client,
                (_, destination, _) =>
                {
                    Directory.CreateDirectory(Path.Combine(destination, "share"));
                    File.WriteAllBytes(Path.Combine(destination, "qemu-system-x86_64.exe"), []);
                    return Task.FromResult(0);
                },
                "https://example.test/qemu.exe",
                expectedHash);

            var result = await service.PrepareAsync(root, null, CancellationToken.None);

            result.IsReady.Should().BeTrue(result.ErrorMessage);
            result.RuntimePath.Should().Be(Path.Combine(root, $"qemu-{QemuRuntimePreparationService.Version}"));
            service.Inspect(root).IsReady.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareRejectsInstallerWithUnexpectedSha512()
    {
        var root = CreateTempDirectory();
        try
        {
            using var client = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("tampered"u8.ToArray())
            }));
            var installerWasStarted = false;
            var service = new QemuRuntimePreparationService(
                client,
                (_, _, _) =>
                {
                    installerWasStarted = true;
                    return Task.FromResult(0);
                },
                "https://example.test/qemu.exe",
                new string('a', 128));

            var result = await service.PrepareAsync(root, null, CancellationToken.None);

            result.IsReady.Should().BeFalse();
            result.ErrorMessage.Should().Contain("checksum");
            installerWasStarted.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareUsesBundledRuntimeWithoutDownloadingInstaller()
    {
        var cacheRoot = CreateTempDirectory();
        var bundledRoot = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(bundledRoot, "share"));
            File.WriteAllBytes(Path.Combine(bundledRoot, "qemu-system-x86_64.exe"), []);
            WriteBundledManifest(bundledRoot, QemuRuntimePreparationService.Version);
            var downloadWasRequested = false;
            using var client = new HttpClient(new StubHttpHandler(_ =>
            {
                downloadWasRequested = true;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }));
            var service = new QemuRuntimePreparationService(
                client,
                installerUrl: "https://example.test/qemu.exe",
                installerSha512: new string('a', 128),
                bundledRuntimePath: bundledRoot);

            var result = await service.PrepareAsync(cacheRoot, null, CancellationToken.None);

            result.IsReady.Should().BeTrue();
            result.RuntimePath.Should().Be(bundledRoot);
            downloadWasRequested.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(bundledRoot, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareDoesNotTrustBundledRuntimeWithUnexpectedVersion()
    {
        var cacheRoot = CreateTempDirectory();
        var bundledRoot = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(bundledRoot, "share"));
            File.WriteAllBytes(Path.Combine(bundledRoot, "qemu-system-x86_64.exe"), []);
            WriteBundledManifest(bundledRoot, "0.0.0");
            var downloadWasRequested = false;
            using var client = new HttpClient(new StubHttpHandler(_ =>
            {
                downloadWasRequested = true;
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }));
            var service = new QemuRuntimePreparationService(
                client,
                installerUrl: "https://example.test/qemu.exe",
                installerSha512: new string('a', 128),
                bundledRuntimePath: bundledRoot);

            var result = await service.PrepareAsync(cacheRoot, null, CancellationToken.None);

            result.IsReady.Should().BeFalse();
            downloadWasRequested.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(cacheRoot, recursive: true);
            Directory.Delete(bundledRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ARuntimeWindowsPartlyClearedFromTempIsDownloadedAgain()
    {
        var root = CreateTempDirectory();
        var installer = "test-qemu-installer"u8.ToArray();
        var expectedHash = Convert.ToHexString(SHA512.HashData(installer)).ToLowerInvariant();
        var unpacks = 0;
        try
        {
            using var client = new HttpClient(new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(installer)
            }));
            var service = new QemuRuntimePreparationService(
                client,
                (_, destination, _) =>
                {
                    unpacks++;
                    Directory.CreateDirectory(Path.Combine(destination, "share", "firmware"));
                    File.WriteAllBytes(Path.Combine(destination, "qemu-system-x86_64.exe"), new byte[64]);
                    File.WriteAllBytes(Path.Combine(destination, "libglib-2.0-0.dll"), new byte[32]);
                    File.WriteAllBytes(Path.Combine(destination, "share", "firmware", "bios.bin"), new byte[16]);
                    return Task.FromResult(0);
                },
                "https://example.test/qemu.exe",
                expectedHash);

            var runtime = (await service.PrepareAsync(root, null, CancellationToken.None)).RuntimePath;
            File.Exists(Path.Combine(runtime, QemuRuntimePreparationService.FileListName)).Should().BeTrue();
            service.Inspect(root).IsReady.Should().BeTrue();

            // What Storage Sense did on a real PC: a library gone, the executable still there.
            File.Delete(Path.Combine(runtime, "libglib-2.0-0.dll"));
            service.Inspect(root).IsReady.Should().BeFalse("a runtime with a missing library cannot start QEMU");

            var again = await service.PrepareAsync(root, null, CancellationToken.None);

            again.IsReady.Should().BeTrue(again.ErrorMessage);
            unpacks.Should().Be(2);
            File.Exists(Path.Combine(runtime, "libglib-2.0-0.dll")).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AFileThatShrankOrARuntimeWithoutAFileListIsNotComplete()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "qemu-system-x86_64.exe"), new byte[64]);
            QemuRuntimePreparationService.IsComplete(root).Should().BeFalse("an older launcher left no list to check against");

            QemuRuntimePreparationService.WriteFileList(root);
            QemuRuntimePreparationService.IsComplete(root).Should().BeTrue();

            File.WriteAllBytes(Path.Combine(root, "qemu-system-x86_64.exe"), new byte[10]);
            QemuRuntimePreparationService.IsComplete(root).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    private static void WriteBundledManifest(string runtimeRoot, string version)
    {
        File.WriteAllText(
            Path.Combine(runtimeRoot, "qemu-runtime-files.json"),
            JsonSerializer.Serialize(new { schemaVersion = 1, version }));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DexTorTests", "Runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }
}
