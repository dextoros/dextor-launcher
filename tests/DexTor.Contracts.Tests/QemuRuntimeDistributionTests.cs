using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace DexTor.Contracts.Tests;

public sealed class QemuRuntimeDistributionTests
{
    private const string Mirror = "https://github.com/dextoros/qemu-runtime/releases/download/qemu-11.1.0-20260811/qemu-w64-setup-20260811.exe";
    private const string Upstream = "https://qemu.weilnetz.de/w64/2026/qemu-w64-setup-20260811.exe";
    private static readonly string Hash = new('A', 128);

    [Fact]
    public void TheMirrorComesFirstThenTheFallbacksInOrder()
    {
        var distribution = QemuRuntimeDistribution.Parse(Manifest(Mirror, [Upstream, "https://third.example.test/q.exe"]));

        distribution.Version.Should().Be("11.1.0");
        distribution.InstallerSha512.Should().Be(new string('a', 128), "the hash is compared in lower case");
        distribution.InstallerUrls.Should().Equal(Mirror, Upstream, "https://third.example.test/q.exe");
    }

    [Fact]
    public void AManifestWithoutFallbacksStillHasItsSourceUrl()
    {
        QemuRuntimeDistribution.Parse(Manifest(Mirror, null)).InstallerUrls.Should().Equal(Mirror);
    }

    [Fact]
    public void DuplicateUrlsAreTriedOnlyOnce()
    {
        var distribution = QemuRuntimeDistribution.Parse(Manifest(Mirror, [Upstream, Mirror, Upstream]));

        distribution.InstallerUrls.Should().Equal(Mirror, Upstream);
    }

    [Theory]
    [InlineData("http://qemu.weilnetz.de/w64/2026/qemu-w64-setup-20260811.exe")]
    [InlineData("https://user:secret@example.test/qemu.exe")]
    [InlineData("https://example.test/qemu.exe#fragment")]
    [InlineData("ftp://example.test/qemu.exe")]
    [InlineData("qemu.exe")]
    [InlineData("")]
    public void AnUnsafeFallbackUrlRejectsTheManifest(string url)
    {
        var parse = () => QemuRuntimeDistribution.Parse(Manifest(Mirror, [url]));

        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void AnHttpSourceUrlRejectsTheManifest()
    {
        var parse = () => QemuRuntimeDistribution.Parse(Manifest("http://example.test/qemu.exe", null));

        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void AMissingOrMalformedHashRejectsTheManifest(string? hash)
    {
        var parse = () => QemuRuntimeDistribution.Parse(Manifest(Mirror, null, hash));

        parse.Should().Throw<InvalidDataException>().WithMessage("*SHA-512*");
    }

    [Fact]
    public void AHashThatIsNotHexRejectsTheManifest()
    {
        var parse = () => QemuRuntimeDistribution.Parse(Manifest(Mirror, null, new string('g', 128)));

        parse.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("""{"schemaVersion":"1.0"}""")]
    [InlineData("""{"schemaVersion":"2.0","runtime":{}}""")]
    [InlineData("""{"schemaVersion":"1.0","runtime":{"version":"11.1.0","installerSha512":"x"}}""")]
    [InlineData("""{"schemaVersion":"1.0","runtime":{"sourceUrl":"https://example.test/q.exe","fallbackSourceUrls":"https://example.test/q.exe"}}""")]
    [InlineData("not json")]
    public void AnIncompleteManifestIsRejectedAsInvalidData(string json)
    {
        var parse = () => QemuRuntimeDistribution.Parse(json);

        parse.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void TheCommittedManifestPutsDexTorsMirrorFirstAndWeilnetzAsFallback()
    {
        var distribution = QemuRuntimeDistribution.Default;

        distribution.InstallerUrls.Should().Equal(Mirror, Upstream);
        distribution.InstallerSha512.Should().HaveLength(128);
    }

    private static string Manifest(string sourceUrl, string[]? fallbacks, string? hash = "default")
    {
        var runtime = new Dictionary<string, object?>
        {
            ["version"] = "11.1.0",
            ["sourceUrl"] = sourceUrl
        };
        if (hash is not null) runtime["installerSha512"] = hash == "default" ? Hash : hash;
        if (fallbacks is not null) runtime["fallbackSourceUrls"] = fallbacks;
        return JsonSerializer.Serialize(new Dictionary<string, object?> { ["schemaVersion"] = "1.0", ["runtime"] = runtime });
    }
}
