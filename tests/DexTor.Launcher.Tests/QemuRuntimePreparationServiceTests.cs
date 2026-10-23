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
    private const string Mirror = "https://mirror.example.test/qemu.exe";
    private const string Fallback = "https://fallback.example.test/qemu.exe";
    private static readonly byte[] Installer = "test-qemu-installer"u8.ToArray();
    private static readonly string InstallerHash = Convert.ToHexString(SHA512.HashData(Installer)).ToLowerInvariant();

    [Fact]
    public async Task TheMirrorIsTriedFirstAndTheFallbackIsNotAskedWhenItAnswers()
    {
        using var fixture = new DownloadFixture(request => Ok(Installer));

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeTrue(result.ErrorMessage);
        fixture.Requested.Should().Equal(Mirror);
        fixture.Installs.Should().Be(1);
    }

    public static TheoryData<string> UnavailableMirrors => new() { "dns", "503", "404", "timeout", "http-redirect" };

    [Theory]
    [MemberData(nameof(UnavailableMirrors))]
    public async Task AnUnavailableMirrorFallsBackToTheNextUrl(string failure)
    {
        using var fixture = new DownloadFixture(request => request.RequestUri!.AbsoluteUri == Mirror
            ? Unavailable(failure, request)
            : Ok(Installer));

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeTrue(result.ErrorMessage);
        fixture.Requested.Should().Equal(Mirror, Fallback);
        fixture.Installs.Should().Be(1);
    }

    [Fact]
    public async Task WhenEveryUrlIsDownNothingIsInstalledAndTheResultSaysSo()
    {
        using var fixture = new DownloadFixture(request => Unavailable("dns", request));

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Could not download");
        fixture.Requested.Should().Equal(Mirror, Fallback);
        fixture.Installs.Should().Be(0);
        fixture.LeftoverFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task ADownloadWithTheWrongHashIsDeletedNotInstalledAndNotRetriedElsewhere()
    {
        using var fixture = new DownloadFixture(request => request.RequestUri!.AbsoluteUri == Mirror
            ? Ok("tampered"u8.ToArray())
            : Ok(Installer));

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeFalse();
        result.ErrorMessage.Should().Contain("checksum");
        fixture.Requested.Should().Equal(Mirror);
        fixture.Installs.Should().Be(0);
        fixture.LeftoverFiles().Should().BeEmpty("the mismatching installer must not stay in the cache");
    }

    [Fact]
    public async Task CancellingStopsAtOnceWithoutTryingTheFallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new DownloadFixture(request =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });

        var prepare = () => fixture.Service.PrepareAsync(fixture.Root, null, cancellation.Token);

        await prepare.Should().ThrowAsync<OperationCanceledException>();
        fixture.Requested.Should().Equal(Mirror);
        fixture.Installs.Should().Be(0);
    }

    [Fact]
    public async Task AStreamThatBreaksMidwayFallsBackToTheNextUrl()
    {
        using var fixture = new DownloadFixture(request => request.RequestUri!.AbsoluteUri == Mirror
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream(Installer[..5])) }
            : Ok(Installer));

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeTrue(result.ErrorMessage);
        fixture.Requested.Should().Equal(Mirror, Fallback);
    }

    [Fact]
    public async Task ADownloadShorterThanItsContentLengthFallsBackToTheNextUrl()
    {
        using var fixture = new DownloadFixture(request =>
        {
            if (request.RequestUri!.AbsoluteUri != Mirror) return Ok(Installer);
            var truncated = Ok(Installer[..5]);
            truncated.Content.Headers.ContentLength = Installer.Length;
            return truncated;
        });

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeTrue(result.ErrorMessage);
        fixture.Requested.Should().Equal(Mirror, Fallback);
    }

    [Fact]
    public async Task ADiskWriteErrorFailsClearlyWithoutTryingOtherUrls()
    {
        using var fixture = new DownloadFixture(
            request => Ok(Installer),
            openDownloadFile: _ => new FullDiskStream());

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Could not save").And.Contain("not enough space");
        fixture.Requested.Should().Equal(Mirror);
        fixture.Installs.Should().Be(0);
    }

    [Fact]
    public async Task AValidCachedInstallerIsReusedWithoutTheNetwork()
    {
        using var fixture = new DownloadFixture(request => Unavailable("dns", request));
        File.WriteAllBytes(Path.Combine(fixture.Root, $"qemu-w64-{QemuRuntimePreparationService.Version}.exe"), Installer);

        var result = await fixture.Service.PrepareAsync(fixture.Root, null, CancellationToken.None);

        result.IsReady.Should().BeTrue(result.ErrorMessage);
        fixture.Requested.Should().BeEmpty();
        fixture.Installs.Should().Be(1);
    }

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Unavailable(string failure, HttpRequestMessage request) => failure switch
    {
        "dns" => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."),
        "503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
        "404" => new HttpResponseMessage(HttpStatusCode.NotFound),
        // What HttpClient.Timeout throws: cancellation that is not the caller's.
        "timeout" => throw new TaskCanceledException("timed out", new TimeoutException()),
        // A redirect chain that ended on plain http must not be trusted, even with a 200.
        "http-redirect" => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(Installer),
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "http://objects.example.test/qemu.exe")
        },
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private sealed class DownloadFixture : IDisposable
    {
        private readonly HttpClient _client;

        public DownloadFixture(Func<HttpRequestMessage, HttpResponseMessage> respond, Func<string, Stream>? openDownloadFile = null)
        {
            Root = CreateTempDirectory();
            _client = new HttpClient(new StubHttpHandler(request =>
            {
                lock (Requested) Requested.Add(request.RequestUri!.AbsoluteUri);
                var response = respond(request);
                response.RequestMessage ??= request;
                return response;
            }));
            Service = new QemuRuntimePreparationService(
                _client,
                (_, destination, _) =>
                {
                    Installs++;
                    Directory.CreateDirectory(Path.Combine(destination, "share"));
                    File.WriteAllBytes(Path.Combine(destination, "qemu-system-x86_64.exe"), []);
                    return Task.FromResult(0);
                },
                Mirror,
                InstallerHash,
                bundledRuntimePath: Path.Combine(Root, "no-bundled-runtime"),
                fallbackInstallerUrls: [Fallback],
                openDownloadFile: openDownloadFile);
        }

        public string Root { get; }
        public QemuRuntimePreparationService Service { get; }
        public List<string> Requested { get; } = [];
        public int Installs { get; private set; }

        public string[] LeftoverFiles() =>
            Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToArray();

        public void Dispose()
        {
            _client.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    /// <summary>Hands out a few bytes, then fails like a connection reset mid-transfer.</summary>
    private sealed class BrokenStream(byte[] head) : MemoryStream(head)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position < Length) return base.ReadAsync(buffer, cancellationToken);
            throw new IOException("An existing connection was forcibly closed by the remote host.");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <summary>A download file on a disk that is full.</summary>
    private sealed class FullDiskStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
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
