using System.Reflection;
using System.IO;

using DexTor.Application;
using DexTor.Creator;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class CreateUsbRecoveryWindowTests
{
    [Fact]
    public void IsManifestOrConfigReleaseErrorReturnsTrueForReleaseErrors()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "IsManifestOrConfigReleaseError",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var releaseResult = new CreatorActionService.CreateUsbResult(
            false,
            string.Empty,
            CreatorActionService.CreateTransaction(),
            "error",
            CreatorStep.PrepareRelease,
            "launcher.create.error.release");

        ((bool)method!.Invoke(null, new object[] { releaseResult })!).Should().BeTrue();

        var manifestResult = new CreatorActionService.CreateUsbResult(
            false,
            string.Empty,
            CreatorActionService.CreateTransaction(),
            "error",
            CreatorStep.PrepareRelease,
            "launcher.create.error.release_manifest_version_missing");

        ((bool)method!.Invoke(null, new object[] { manifestResult })!).Should().BeTrue();
    }

    [Fact]
    public void IsManifestOrConfigReleaseErrorReturnsFalseForNonReleaseErrors()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "IsManifestOrConfigReleaseError",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var simulationResult = new CreatorActionService.CreateUsbResult(
            false,
            string.Empty,
            CreatorActionService.CreateTransaction(),
            "error",
            CreatorStep.FormatUsb,
            "launcher.create.error.simulation");

        ((bool)method!.Invoke(null, new object[] { simulationResult })!).Should().BeFalse();

        var successResult = new CreatorActionService.CreateUsbResult(
            true,
            string.Empty,
            CreatorActionService.CreateTransaction());

        ((bool)method!.Invoke(null, new object[] { successResult })!).Should().BeFalse();
    }

    [Fact]
    public void IsReleaseErrorReturnsTrueForReleasePackageAndConfigErrors()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "IsReleaseError",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var releaseResult = new CreatorActionService.CreateUsbResult(
            false,
            string.Empty,
            CreatorActionService.CreateTransaction(),
            "error",
            CreatorStep.PrepareRelease,
            "launcher.create.error.release");
        ((bool)method!.Invoke(null, new object[] { releaseResult })!).Should().BeTrue();

        var configResult = new CreatorActionService.CreateUsbResult(
            false,
            string.Empty,
            CreatorActionService.CreateTransaction(),
            "error",
            CreatorStep.PrepareRelease,
            "launcher.create.error.release_config");
        ((bool)method!.Invoke(null, new object[] { configResult })!).Should().BeTrue();

        var manifestResult = new CreatorActionService.CreateUsbResult(
            false,
            string.Empty,
            CreatorActionService.CreateTransaction(),
            "error",
            CreatorStep.PrepareRelease,
            "launcher.create.error.release_manifest_version_missing");
        ((bool)method!.Invoke(null, new object[] { manifestResult })!).Should().BeFalse();
    }

    [Fact]
    public void TryGetRecoveryTargetReturnsFolderForExistingFile()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "TryGetRecoveryTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var tempDirectory = Path.Combine(Path.GetTempPath(), "DexTorRecovery", Guid.NewGuid().ToString("N"));
        var manifest = Path.Combine(tempDirectory, "release-manifest.json");
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(manifest, "{}");

        try
        {
            object?[] args = new object?[] { manifest, null };
            ((bool)method!.Invoke(null, args)!).Should().BeTrue();
            ((string?)args[1]).Should().Be(tempDirectory);
        }
        finally
        {
            Directory.Delete(tempDirectory, true);
        }
    }

    [Fact]
    public void TryGetRecoveryTargetReturnsTargetForHttpUrl()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "TryGetRecoveryTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var url = "https://example.org/release";
        object?[] args = new object?[] { url, null };
        ((bool)method!.Invoke(null, args)!).Should().BeTrue();
        ((string?)args[1]).Should().Be(url);
    }

    [Fact]
    public void TryGetRecoveryTargetReturnsTargetForFileUrl()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "TryGetRecoveryTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        var tempDirectory = Path.Combine(Path.GetTempPath(), "DexTorRecovery", Guid.NewGuid().ToString("N"));
        var manifest = Path.Combine(tempDirectory, "release-manifest.json");
        Directory.CreateDirectory(tempDirectory);
        File.WriteAllText(manifest, "{}");

        try
        {
            var fileUri = new Uri(manifest).AbsoluteUri;
            object?[] args = new object?[] { fileUri, null };
            ((bool)method!.Invoke(null, args)!).Should().BeTrue();
            ((string?)args[1]).Should().Be(tempDirectory);

            var localhostUri = $"file://localhost/{manifest.Replace('\\', '/')}";
            object?[] argsLocalhost = new object?[] { localhostUri, null };
            ((bool)method!.Invoke(null, argsLocalhost)!).Should().BeTrue();
            ((string?)argsLocalhost[1]).Should().Be(tempDirectory);
        }
        finally
        {
            Directory.Delete(tempDirectory, true);
        }
    }

    [Fact]
    public void TryGetRecoveryTargetReturnsFalseForUnsupportedScheme()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "TryGetRecoveryTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        object?[] args = new object?[] { "ftp://example.org/release", null };
        ((bool)method!.Invoke(null, args)!).Should().BeFalse();
        args[1].Should().BeNull();
    }

    [Fact]
    public void TryGetRecoveryTargetReturnsFalseForNonExistentPath()
    {
        var method = typeof(CreateUsbRecoveryWindow).GetMethod(
            "TryGetRecoveryTarget",
            BindingFlags.NonPublic | BindingFlags.Static);

        method.Should().NotBeNull();

        object?[] args = new object?[] { Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), null };
        ((bool)method!.Invoke(null, args)!).Should().BeFalse();
        args[1].Should().BeNull();
    }
}
