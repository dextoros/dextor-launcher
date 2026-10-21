using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

using DexTor.Release;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class LiveImagePreparationServiceTests
{
    /// <summary>A release key made for the tests; the real private key never leaves the OS team.</summary>
    private static readonly ECDsa TestPrivateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static readonly ImageSigningKey TestKey =
        ImageSigningKey.FromPem("DexTor OS Test", TestPrivateKey.ExportSubjectPublicKeyInfoPem());

    /// <summary>What openssl dgst -sha256 -sign writes for <paramref name="image"/>: a DER sequence.</summary>
    private static byte[] Sign(byte[] image, ECDsa? key = null) =>
        (key ?? TestPrivateKey).SignHash(SHA256.HashData(image), DSASignatureFormat.Rfc3279DerSequence);

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    [Fact]
    public async Task OnePassProducesTheSha256AndReportsBytesChecked()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var bytes = new byte[9 * 1024 * 1024 + 17];
            new Random(7).NextBytes(bytes);
            var path = Path.Combine(cacheRoot, "image.iso");
            await File.WriteAllBytesAsync(path, bytes);
            var reports = new List<LiveImagePreparationProgress>();
            var progress = new SynchronousProgress<LiveImagePreparationProgress>(reports.Add);

            var sha256 = await LiveImagePreparationService.ComputeSha256Async(
                path, progress, LiveImagePreparationService.StageVerifySha256, CancellationToken.None);

            sha256.Should().Be(Sha256Hex(bytes));
            reports.Should().NotBeEmpty();
            reports.Should().OnlyContain(report => report.TotalBytes == bytes.Length
                && report.Stage == LiveImagePreparationService.StageVerifySha256);
            reports[^1].BytesDownloaded.Should().Be(bytes.Length);
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public void TheSha256AndTheSignatureAreLookedForNextToTheImage()
    {
        var info = LiveImagePreparationService.ParseSourceInfo("https://dextoros.com/public/1.1.45/DextorOS_1_1_45.iso\n");

        info!.Sha256Url.Should().Be("https://dextoros.com/public/1.1.45/DextorOS_1_1_45.iso.sha256");
        info.SignatureUrl.Should().Be("https://dextoros.com/public/1.1.45/DextorOS_1_1_45.iso.sig");
        info.SourceFileName.Should().Be("DextorOS_1_1_45.iso");
    }

    [Theory]
    [InlineData("fdcbcfa2420f7d80fd4aadd4283a8bd1a3c4d35eb86ff05b0e9ac776c51169ae  DextorOS_1_1_45.iso\n")]
    [InlineData("FDCBCFA2420F7D80FD4AADD4283A8BD1A3C4D35EB86FF05B0E9AC776C51169AE *DextorOS_1_1_45.iso")]
    public void ASha256sumLineGivesItsDigest(string text)
    {
        LiveImagePreparationService.ParseSha256FromText(text)
            .Should().Be("fdcbcfa2420f7d80fd4aadd4283a8bd1a3c4d35eb86ff05b0e9ac776c51169ae");
    }

    [Fact]
    public void AnMd5LineIsNotTakenForASha256()
    {
        LiveImagePreparationService.ParseSha256FromText("5e956918c41b84b19f1375670cada718  DextorOS_1_1_45.iso").Should().BeNull();
    }

    private const string ImageUrl = "https://example.test/latest.iso";
    private const string ImageTag = "\"image-v1\"";

    /// <summary>A server for one signed image that honours Range and If-Range like Cloudflare does.</summary>
    private static HttpClient ImageServer(byte[] image, string etag, List<HttpRequestMessage> requests, int? failAfterBytes = null)
    {
        var sha256 = Sha256Hex(image);
        var signature = Sign(image);
        return new HttpClient(new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("lastversion.link", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ImageUrl) };
            }

            if (path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sha256) };
            }

            if (path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(signature) };
            }

            requests.Add(request);
            var ifRange = request.Headers.TryGetValues("If-Range", out var values) ? values.Single() : null;
            var from = request.Headers.Range?.Ranges.Single().From;
            var body = from is { } start && ifRange == etag ? image[(int)start..] : image;
            Stream stream = new MemoryStream(body);
            if (failAfterBytes is { } limit) stream = new FailingStream(body, limit);
            var response = new HttpResponseMessage(body.Length == image.Length ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentLength = body.Length;
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from!.Value, image.Length - 1, image.Length);
            }
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
            return response;
        }));
    }

    private sealed class FailingStream(byte[] data, int limit) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= limit) throw new IOException("The connection was reset.");
            return base.Read(buffer, offset, (int)Math.Min(count, limit - Position));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= limit) throw new IOException("The connection was reset.");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - Position)], cancellationToken);
        }
    }

    private static byte[] RandomImage(int length)
    {
        var bytes = new byte[length];
        new Random(11).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task ADroppedConnectionKeepsWhatArrivedForTheNextAttempt()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var image = RandomImage(3 * 1024 * 1024);
            var requests = new List<HttpRequestMessage>();
            using var client = ImageServer(image, ImageTag, requests, failAfterBytes: 2 * 1024 * 1024 + 5);

            var result = await new LiveImagePreparationService(client, TestKey).PrepareFromIndexAsync(cacheRoot);

            result.Success.Should().BeFalse();
            var partial = Path.Combine(cacheRoot, "latest.iso.tmp");
            File.Exists(partial).Should().BeTrue();
            new FileInfo(partial).Length.Should().Be(2 * 1024 * 1024 + 5);
            File.ReadAllLines(partial + ".source").Should().Equal(ImageUrl, ImageTag);
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public async Task ThePartialImageIsContinuedFromWhereItStoppedWithTheLastMegabyteFetchedAgain()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var image = RandomImage(3 * 1024 * 1024);
            var partialLength = 2 * 1024 * 1024 + 5;
            await File.WriteAllBytesAsync(Path.Combine(cacheRoot, "latest.iso.tmp"), image[..partialLength]);
            await File.WriteAllLinesAsync(Path.Combine(cacheRoot, "latest.iso.tmp.source"), [ImageUrl, ImageTag]);
            var requests = new List<HttpRequestMessage>();
            using var client = ImageServer(image, ImageTag, requests);
            var reports = new List<LiveImagePreparationProgress>();

            var result = await new LiveImagePreparationService(client, TestKey).PrepareFromIndexAsync(
                cacheRoot, new SynchronousProgress<LiveImagePreparationProgress>(reports.Add));

            result.Success.Should().BeTrue(result.ErrorMessage);
            File.ReadAllBytes(result.LocalImagePath!).Should().Equal(image);
            requests.Should().ContainSingle();
            requests[0].Headers.Range!.Ranges.Single().From.Should().Be(partialLength - LiveImagePreparationService.ResumeOverlapBytes);
            requests[0].Headers.GetValues("If-Range").Should().Equal(ImageTag);
            reports.Should().Contain(report => report.Stage == LiveImagePreparationService.StageResume);
            reports.Where(report => report.Stage == LiveImagePreparationService.StageDownload && report.TotalBytes is not null)
                .Should().OnlyContain(report => report.TotalBytes == image.Length && report.BytesDownloaded >= partialLength - LiveImagePreparationService.ResumeOverlapBytes);
            File.Exists(result.LocalImagePath + ".tmp").Should().BeFalse();
            File.Exists(result.LocalImagePath + ".tmp.source").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public async Task APartialFileOfAnImageThatChangedOnTheServerIsNeverSplicedIn()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var oldImage = RandomImage(3 * 1024 * 1024);
            var newImage = oldImage.Select(value => (byte)(value ^ 0x5a)).ToArray();
            await File.WriteAllBytesAsync(Path.Combine(cacheRoot, "latest.iso.tmp"), oldImage[..(2 * 1024 * 1024)]);
            await File.WriteAllLinesAsync(Path.Combine(cacheRoot, "latest.iso.tmp.source"), [ImageUrl, ImageTag]);
            var requests = new List<HttpRequestMessage>();
            using var client = ImageServer(newImage, "\"image-v2\"", requests);

            var result = await new LiveImagePreparationService(client, TestKey).PrepareFromIndexAsync(cacheRoot);

            result.Success.Should().BeTrue(result.ErrorMessage);
            File.ReadAllBytes(result.LocalImagePath!).Should().Equal(newImage, "the server sent the whole new file");
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task InspectionRecognizesTheCachedImageTheReleaseKeySigned()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("verified DexTor image");
            await File.WriteAllBytesAsync(Path.Combine(cacheRoot, "latest.iso"), bytes);
            using var client = CreateClient(Sha256Hex(bytes), Sign(bytes));
            var service = new LiveImagePreparationService(client, TestKey);

            var result = await service.InspectCacheFromIndexAsync(cacheRoot);

            result.Success.Should().BeTrue();
            result.IsReady.Should().BeTrue();
            result.LocalImagePath.Should().Be(Path.Combine(cacheRoot, "latest.iso"));
            result.SourceSha256.Should().Be(Sha256Hex(bytes));
            result.SourceSizeBytes.Should().Be(bytes.Length);
            result.SigningKey.Should().Be("DexTor OS Test · " + TestKey.Fingerprint);
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public async Task InspectionRequestsDownloadWhenCurrentImageIsMissingOrInvalid()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(cacheRoot, "latest.iso"), "outdated image");
            var current = Encoding.UTF8.GetBytes("current image");
            using var client = CreateClient(Sha256Hex(current), Sign(current));
            var service = new LiveImagePreparationService(client, TestKey);

            var result = await service.InspectCacheFromIndexAsync(cacheRoot);

            result.Success.Should().BeTrue();
            result.IsReady.Should().BeFalse();
            result.SourceUrl.Should().Be("https://example.test/latest.iso");
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public async Task ACachedImageWhoseSignatureFailsIsDeletedAndReportedAsSuch()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var bytes = Encoding.UTF8.GetBytes("image signed by someone else");
            var cached = Path.Combine(cacheRoot, "latest.iso");
            await File.WriteAllBytesAsync(cached, bytes);
            using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var client = CreateClient(Sha256Hex(bytes), Sign(bytes, stranger));

            var result = await new LiveImagePreparationService(client, TestKey).InspectCacheFromIndexAsync(cacheRoot);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be(LiveImagePreparationService.SignatureFailedKey);
            File.Exists(cached).Should().BeFalse("a file the release key did not sign is not kept");
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public async Task NothingIsDownloadedWhenTheSignatureIsMissing()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var image = Encoding.UTF8.GetBytes("unsigned image");
            var imageRequests = 0;
            using var client = new HttpClient(new StubHttpHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("lastversion.link", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ImageUrl) };
                if (path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sha256Hex(image)) };
                if (path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                imageRequests++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(image) };
            }));

            var result = await new LiveImagePreparationService(client, TestKey).PrepareFromIndexAsync(cacheRoot);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be(LiveImagePreparationService.SignatureFailedKey);
            imageRequests.Should().Be(0, "a release without a valid signature is not worth the download");
            Directory.EnumerateFiles(cacheRoot).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public async Task AnImageThatDoesNotHashToTheSignedSha256IsDeleted()
    {
        var cacheRoot = CreateTempDirectory();
        try
        {
            var signed = Encoding.UTF8.GetBytes("the image the release key signed");
            var served = Encoding.UTF8.GetBytes("a different image on the wire");
            using var client = new HttpClient(new StubHttpHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("lastversion.link", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ImageUrl) };
                if (path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Sha256Hex(signed)) };
                if (path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Sign(signed)) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) };
            }));

            var result = await new LiveImagePreparationService(client, TestKey).PrepareFromIndexAsync(cacheRoot);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be(LiveImagePreparationService.ChecksumFailedKey);
            File.Exists(Path.Combine(cacheRoot, "latest.iso")).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    [Fact]
    public async Task InspectionFailsWhenTempCacheCannotBeCreatedAsDirectory()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var current = Encoding.UTF8.GetBytes("current image");
            using var client = CreateClient(Sha256Hex(current), Sign(current));
            var service = new LiveImagePreparationService(client, TestKey);

            var result = await service.InspectCacheFromIndexAsync(tempFile);

            result.Success.Should().BeFalse();
            result.IsReady.Should().BeFalse();
            result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task DownloadClosesTemporaryFileBeforePromotingItToCachedIso()
    {
        var cacheRoot = CreateTempDirectory();
        var imageBytes = Encoding.UTF8.GetBytes("downloaded DexTor image");
        try
        {
            using var client = CreateClient(Sha256Hex(imageBytes), Sign(imageBytes), imageBytes);
            var service = new LiveImagePreparationService(client, TestKey);

            var result = await service.PrepareFromIndexAsync(cacheRoot);

            result.Success.Should().BeTrue(result.ErrorMessage);
            result.LocalImagePath.Should().Be(Path.Combine(cacheRoot, "latest.iso"));
            result.SigningKey.Should().Contain(TestKey.Fingerprint);
            File.ReadAllBytes(result.LocalImagePath!).Should().Equal(imageBytes);
            File.Exists(result.LocalImagePath + ".tmp").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(cacheRoot, true);
        }
    }

    private static HttpClient CreateClient(string sha256, byte[] signature, byte[]? image = null)
    {
        return new HttpClient(new StubHttpHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpContent content = path.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
                ? new StringContent(sha256 + "  latest.iso")
                : path.EndsWith(".sig", StringComparison.OrdinalIgnoreCase)
                    ? new ByteArrayContent(signature)
                    : path.EndsWith("lastversion.link", StringComparison.OrdinalIgnoreCase) || image is null
                        ? new StringContent("https://example.test/latest.iso")
                        : new ByteArrayContent(image);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "DexTorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }
}

public sealed class ImageSigningKeyTests
{
    private static readonly ECDsa PrivateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly ImageSigningKey Key = ImageSigningKey.FromPem("Test", PrivateKey.ExportSubjectPublicKeyInfoPem());
    private static readonly byte[] Digest = SHA256.HashData("DexTor OS image"u8);

    [Fact]
    public void TheOpensslDerSignatureIsAccepted() =>
        Key.Verify(Digest, PrivateKey.SignHash(Digest, DSASignatureFormat.Rfc3279DerSequence)).Should().BeTrue();

    [Fact]
    public void TheRawSignatureIsAccepted() =>
        Key.Verify(Digest, PrivateKey.SignHash(Digest, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).Should().BeTrue();

    [Fact]
    public void ASignatureUploadedAsBase64TextIsAccepted()
    {
        var text = Convert.ToBase64String(PrivateKey.SignHash(Digest, DSASignatureFormat.Rfc3279DerSequence)) + "\n";
        Key.Verify(Digest, Encoding.ASCII.GetBytes(text)).Should().BeTrue();
    }

    [Fact]
    public void AnotherImageOrAnotherKeyIsRejected()
    {
        var signature = PrivateKey.SignHash(Digest, DSASignatureFormat.Rfc3279DerSequence);
        Key.Verify(SHA256.HashData("another image"u8), signature).Should().BeFalse();
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Key.Verify(Digest, stranger.SignHash(Digest, DSASignatureFormat.Rfc3279DerSequence)).Should().BeFalse();
        Key.Verify(Digest, new byte[ImageSigningKey.MaxSignatureBytes + 1]).Should().BeFalse();
        Key.Verify(Digest, "not a signature"u8).Should().BeFalse();
    }

    [Fact]
    public void TheReleaseKeyIsBuiltInWithItsFingerprint()
    {
        ImageSigningKey.Release.Name.Should().Be("DexTor OS Release");
        ImageSigningKey.Release.Fingerprint.Should().Be("E246 B0C5 33BA 5EE8 939A 45FC 3C38 F706 E288 E56D");
    }
}
