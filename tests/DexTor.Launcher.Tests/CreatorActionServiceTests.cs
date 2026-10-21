using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using DexTor.Creator;
using DexTor.Launcher;
using DexTor.Release;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class CreatorActionServiceTests
{
    private static readonly object ReleaseEnvLock = new();

    [Fact]
    public void RunSimulationCompletesTransaction()
    {
        var tx = CreatorActionService.CreateTransaction();

        CreatorActionService.RunSimulation(tx);

        tx.Current.Should().Be(CreatorStep.Complete);
        tx.LastCompleted.Should().Be(CreatorStep.Complete);
    }

    [Fact]
    public void RunCreateFlowReportsStepsInOrder()
    {
        var steps = new List<CreatorStep>();
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var result = CreatorActionService.RunCreateFlow(path, new Progress<CreatorStep>(step => steps.Add(step)));

            result.Success.Should().BeTrue();
            steps.Should().Equal(
                CreatorStep.PrepareRelease,
                CreatorStep.ConfirmErase,
                CreatorStep.FormatUsb,
                CreatorStep.WriteSystemImage,
                CreatorStep.CreateDataImage,
                CreatorStep.VerifyUsb,
                CreatorStep.ProvisionGuest,
                CreatorStep.Complete);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void RunCreateFlowReportsWarningDuringVerificationStep()
    {
        var events = new List<CreatorActionService.CreatorStepProgress>();
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var result = CreatorActionService.RunCreateFlow(
                path,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => events.Add(stepProgress)));

            result.Success.Should().BeTrue();
            events.Should().Contain(
                progress => progress.Step == CreatorStep.VerifyUsb && progress.Status == CreatorActionService.CreatorStepStatus.Warning);
            events.Single(progress =>
                    progress.Step == CreatorStep.VerifyUsb &&
                    progress.Status == CreatorActionService.CreatorStepStatus.Warning)
                .MessageKey
                .Should().Be("launcher.create.warning.verify_usb");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentReturnsFalseWhenMissing()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, null);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().BeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentReturnsErrorWhenPartialConfig()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, Path.Combine(Path.GetTempPath(), "dextor_release_source"));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().Be("launcher.create.error.release_config");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentReturnsConfigWhenFullyProvided()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source");
            var expectedSource = Path.Combine(sourceDir, "system.img");
            var expectedSha = new string('A', 64);
            var cacheRoot = Path.Combine(Path.GetTempPath(), "dextor_release_cache", Path.GetRandomFileName());

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(expectedSource, "dextor-release-source");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, expectedSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, expectedSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, cacheRoot);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeTrue();

                config!.Source.Should().Be(expectedSource);
                config.Sha256.Should().Be(expectedSha);
                config.DestinationPath.Should().Be(Path.Combine(cacheRoot, CreatorActionService.DefaultReleaseCacheFile));
                errorKey.Should().BeNull();
                Directory.Exists(cacheRoot).Should().BeTrue();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
                if (Directory.Exists(cacheRoot))
                {
                    Directory.Delete(cacheRoot, true);
                }

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentResolvesFileUriSource()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source");
            var sourcePath = Path.Combine(sourceDir, "system.img");

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, "dextor-release-source");
                var sourceUri = new Uri(sourcePath);

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourceUri.AbsoluteUri);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeTrue();

                config.Should().NotBeNull();
                config!.Source.Should().Be(Path.GetFullPath(sourcePath));
                config.Sha256.Should().Be(new string('A', 64));
                errorKey.Should().BeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentResolvesLocalhostFileUriSource()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source");
            var sourcePath = Path.Combine(sourceDir, "system image.img");

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, "dextor-release-source");
                var encodedPath = Path.GetFullPath(sourcePath)
                    .Replace("\\", "/", StringComparison.InvariantCulture)
                    .Replace(":", "%3A");
                var sourceUri = $"file://localhost/{encodedPath}";

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourceUri);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeTrue();

                config.Should().NotBeNull();
                config!.Source.Should().Be(Path.GetFullPath(sourcePath));
                config.Sha256.Should().Be(new string('A', 64));
                errorKey.Should().BeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentResolvesHttpSource()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            try
            {
                var sourceUri = "https://example.com/releases/dextor/system.img";

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourceUri);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeTrue();

                config.Should().NotBeNull();
                config!.Source.Should().Be(sourceUri);
                config.Sha256.Should().Be(new string('A', 64));
                errorKey.Should().BeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentResolvesImplicitManifestForLocalSource()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source_manifest");
            var sourcePath = Path.Combine(sourceDir, "system.img");
            var manifestPath = Path.Combine(sourceDir, "release-manifest.json");

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, "dextor-release-source");
                File.WriteAllText(manifestPath, "{}");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourcePath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                        out var config,
                        out var errorKey)
                    .Should().BeTrue();

                config!.ManifestPath.Should().Be(Path.GetFullPath(manifestPath));
                errorKey.Should().BeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentUsesExplicitManifestPath()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source_manifest");
            var sourcePath = Path.Combine(sourceDir, "system.img");
            var manifestPath = Path.Combine(sourceDir, "custom-manifest.json");
            var fallbackManifestPath = Path.Combine(sourceDir, "release-manifest.json");

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, "dextor-release-source");
                File.WriteAllText(manifestPath, "{}");
                File.WriteAllText(fallbackManifestPath, "{}");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourcePath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, manifestPath);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                        out var config,
                        out var errorKey)
                    .Should().BeTrue();

                config!.ManifestPath.Should().Be(Path.GetFullPath(manifestPath));
                errorKey.Should().BeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentRejectsInvalidManifest()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source_manifest");
            var sourcePath = Path.Combine(sourceDir, "system.img");

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, "dextor-release-source");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourcePath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, "file://bad-host/wrong.json");

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                        out var config,
                        out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().Be("launcher.create.error.release_config");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }
            }
        }
    }

    [Fact]
    public void GetReleaseConfigRecoveryPathReturnsNullWhenNotConfigured()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);

            try
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, null);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, null);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, null);

                CreatorActionService.GetReleaseConfigRecoveryPath().Should().BeNull();
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
            }
        }
    }

    [Fact]
    public void GetReleaseConfigRecoveryPathPrefersLocalSourcePathOverManifestWhenConfigured()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);

            var tempRoot = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
            var manifestPath = Path.Combine(tempRoot, "release-manifest.json");
            var sourcePath = Path.Combine(tempRoot, "dextor-release.bin");

            try
            {
                Directory.CreateDirectory(tempRoot);
                File.WriteAllText(sourcePath, "local release source");
                File.WriteAllText(manifestPath, "{}");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourcePath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, manifestPath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));

                CreatorActionService.GetReleaseConfigRecoveryPath().Should().Be(Path.GetFullPath(sourcePath));
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);

                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }
    }

    [Fact]
    public void GetReleaseConfigRecoveryPathResolvesLocalhostFileUriSource()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);

            var tempRoot = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
            var sourcePath = Path.Combine(tempRoot, "dextor-release.bin");

            try
            {
                Directory.CreateDirectory(tempRoot);
                File.WriteAllText(sourcePath, "local release source");

                var encodedPath = Path.GetFullPath(sourcePath)
                    .Replace("\\", "/", StringComparison.InvariantCulture)
                    .Replace(":", "%3A");
                var sourceUri = $"file://localhost/{encodedPath}";
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourceUri);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, null);

                CreatorActionService.GetReleaseConfigRecoveryPath().Should().Be(Path.GetFullPath(sourcePath));
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);

                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }
    }

    [Fact]
    public void GetReleaseConfigRecoveryPathReturnsRemoteSourceWhenSourceIsRemote()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);

            var tempRoot = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
            var manifestPath = Path.Combine(tempRoot, "release-manifest.json");

            try
            {
                Directory.CreateDirectory(tempRoot);
                File.WriteAllText(manifestPath, "{}");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, "https://example.com/release/system.img");
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, manifestPath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));

                CreatorActionService.GetReleaseConfigRecoveryPath().Should().Be("https://example.com/release/system.img");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, true);
                }
            }
        }
    }

    [Fact]
    public void GetReleaseConfigRecoveryPathReturnsRemoteSourceWithoutManifest()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalManifest = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, "https://example.com/release/system.img");
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, null);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);

                CreatorActionService.GetReleaseConfigRecoveryPath().Should().Be("https://example.com/release/system.img");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseManifestEnvVar, originalManifest);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
            }
        }
    }

    [Theory]
    [InlineData("relative/source.img")]
    [InlineData("dextor://bad-protocol.example/system.img")]
    [InlineData("file:///@/not_a_path")]
    [InlineData("C:\\\\not-a-real-file.img")]
    public void TryGetReleaseConfigFromEnvironmentReturnsErrorWhenInvalidSource(string invalidSource)
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, invalidSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().Be("launcher.create.error.release_config");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentReturnsErrorWhenInvalidReleaseCachePath()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source");
            var sourcePath = Path.Combine(sourceDir, "system.img");
            var invalidCache = "relative\\dextor-cache";

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, "dextor-release-source");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourcePath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, invalidCache);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                        out var config,
                        out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().Be("launcher.create.error.release_config");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentReturnsErrorWhenReleaseCachePathNotWritable()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            var sourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source");
            var sourcePath = Path.Combine(sourceDir, "system.img");
            var blockedCache = Path.Combine(Path.GetTempPath(), "dextor_release_cache_blocker.file");

            try
            {
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(sourcePath, "dextor-release-source");
                File.WriteAllText(blockedCache, "dextor-cache-blocker");

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, sourcePath);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, blockedCache);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                        out var config,
                        out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().Be("launcher.create.error.release_config");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);

                if (Directory.Exists(sourceDir))
                {
                    Directory.Delete(sourceDir, true);
                }

                if (File.Exists(blockedCache))
                {
                    File.Delete(blockedCache);
                }
            }
        }
    }

    [Fact]
    public void TryGetReleaseConfigFromEnvironmentReturnsErrorWhenMissingRootedSourceFile()
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            var missingSource = Path.Combine(Path.GetTempPath(), "dextor_missing_release_source.img");

            try
            {
                if (File.Exists(missingSource))
                {
                    File.Delete(missingSource);
                }

                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, missingSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('A', 64));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                        out var config,
                        out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().Be("launcher.create.error.release_config");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
            }
        }
    }

    [Theory]
    [InlineData("short")]
    [InlineData("Z1234567890ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123")]
    public void TryGetReleaseConfigFromEnvironmentReturnsErrorWhenInvalidSha(string invalidSha)
    {
        lock (ReleaseEnvLock)
        {
            var originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
            var originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
            var originalCache = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar);

            try
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, Path.Combine(Path.GetTempPath(), "dextor_release_source"));
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, invalidSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, null);

                CreatorActionService.TryGetReleaseConfigFromEnvironment(
                    out var config,
                    out var errorKey)
                    .Should().BeFalse();

                config.Should().BeNull();
                errorKey.Should().Be("launcher.create.error.release_config");
            }
            finally
            {
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, originalCache);
            }
        }
    }

    [Fact]
    public void RunCreateFlowReportsErrorWhenSimulationFails()
    {
        var events = new List<CreatorActionService.CreatorStepProgress>();
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var result = CreatorActionService.RunCreateFlow(
                path,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => events.Add(stepProgress)),
                failStep: step => step == CreatorStep.VerifyUsb);

            result.Success.Should().BeFalse();
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.possibly_modified");
            events.Should().Contain(
                progress => progress.Step == CreatorStep.VerifyUsb && progress.Status == CreatorActionService.CreatorStepStatus.Error);
            var eventWithError = events.Single(progress =>
                progress.Step == CreatorStep.VerifyUsb &&
                progress.Status == CreatorActionService.CreatorStepStatus.Error);
            eventWithError.MessageKey.Should().Be("launcher.create.error.simulation");
            eventWithError.MessageArgs.Should().NotBeNull();
            eventWithError.MessageArgs!.ContainsKey("error").Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Theory]
    [InlineData(CreatorStep.ConfirmErase)]
    [InlineData(CreatorStep.FormatUsb)]
    [InlineData(CreatorStep.WriteSystemImage)]
    [InlineData(CreatorStep.CreateDataImage)]
    [InlineData(CreatorStep.VerifyUsb)]
    [InlineData(CreatorStep.ProvisionGuest)]
    [InlineData(CreatorStep.Complete)]
    public void RunCreateFlowReportsErrorSafetyWhenSimulationFailsAtAnyStep(CreatorStep failedStep)
    {
        var events = new List<CreatorActionService.CreatorStepProgress>();
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var result = CreatorActionService.RunCreateFlow(
                path,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => events.Add(stepProgress)),
                failStep: step => step == failedStep,
                stepDelayMs: 0);

            result.Success.Should().BeFalse();
            result.Transaction.Current.Should().Be(failedStep);
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.possibly_modified");

            var eventWithError = events.Single(
                progress => progress.Step == failedStep &&
                    progress.Status == CreatorActionService.CreatorStepStatus.Error);
            eventWithError.MessageKey.Should().Be("launcher.create.error.simulation");
            eventWithError.MessageArgs.Should().NotBeNull();
            eventWithError.MessageArgs!.ContainsKey("error").Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void RunCreateFlowWritesDexTorMarkerFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        try
        {
            var result = CreatorActionService.RunCreateFlow(path);

            result.Success.Should().BeTrue();
            File.Exists(Path.Combine(result.UsbRootPath, "DEXTOR", "metadata", "device.json")).Should().BeTrue();
            var startHtmlPath = Path.Combine(result.UsbRootPath, "START DEXTOR ON WINDOWS.html");
            File.Exists(startHtmlPath).Should().BeTrue();
            var startHtml = File.ReadAllText(startHtmlPath);
            startHtml.Should().Contain("https://dextoros.com/download.html");
            startHtml.Should().NotContain("dextor.dev");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncDownloadsReleaseBeforeCreate()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var steps = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
            var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(step => steps.Add(step)),
                failStep: null);

            result.Success.Should().BeTrue();
            steps.Should().NotBeEmpty();
            steps.First().Step.Should().Be(CreatorStep.PrepareRelease);
            File.Exists(releaseDestination).Should().BeTrue();
            var metadataPath = Path.Combine(path, "DEXTOR", "metadata", "device.json");
            File.Exists(metadataPath).Should().BeTrue();
            File.Exists(Path.Combine(path, "START DEXTOR ON WINDOWS.html")).Should().BeTrue();

            var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
            var root = metadata.RootElement;
            root.GetProperty("release_image_path").GetString().Should().Be(Path.GetFullPath(releaseDestination));
            root.GetProperty("release_source").GetString().Should().Be(Path.GetFullPath(releaseSource));
            root.GetProperty("release_sha256").GetString().Should().Be(expectedSha);
            root.GetProperty("release_image_from_cache").GetBoolean().Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReportsErrorOnHashMismatch()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var steps = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
                await File.WriteAllBytesAsync(releaseSource, Encoding.UTF8.GetBytes("bad-payload"));

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                releaseDestination,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(step => steps.Add(step)),
                failStep: null);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
            steps.Should().Contain(
                progress => progress.Step == CreatorStep.PrepareRelease && progress.Status == CreatorActionService.CreatorStepStatus.Error);
            steps.Single(
                progress => progress.Step == CreatorStep.PrepareRelease && progress.Status == CreatorActionService.CreatorStepStatus.Error).MessageKey
                .Should().Be("launcher.create.error.release");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorFromDownloader()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");

        const string downloadError = "download service unavailable";

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            File.WriteAllText(releaseSource, "mock release source");
            var fakeDownloader = new FailingReleaseDownloader(downloadError);

            string? originalSource;
            string? originalSha;
            lock (ReleaseEnvLock)
            {
                originalSource = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar);
                originalSha = Environment.GetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, releaseSource);
                Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, new string('b', 64));
            }

            try
            {
                var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                    path,
                    releaseSource,
                    new string('b', 64),
                    releaseDestination,
                    null,
                    failStep: null,
                    releaseDownloader: fakeDownloader);

                result.Success.Should().BeFalse();
                result.ErrorMessageKey.Should().Be("launcher.create.error.release");
                result.ErrorMessage.Should().Contain(downloadError);
                result.RecoveryPath.Should().Be(Path.GetFullPath(releaseSource));
                result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
            }
            finally
            {
                lock (ReleaseEnvLock)
                {
                    Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, originalSource);
                    Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, originalSha);
                }
            }
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorWhenResumedImageIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");

        var transaction = CreatorActionService.CreateTransaction();
        transaction.MarkCompleted(CreatorStep.PrepareRelease);
        transaction.MarkCompleted(CreatorStep.ConfirmErase);
        transaction.Interrupt();

        try
        {
            Directory.CreateDirectory(releaseSourceDir);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                new string('c', 64),
                releaseDestination,
                null,
                failStep: null,
                stepDelayMs: 0,
                releaseDownloader: new FailingReleaseDownloader("should not be used"),
                transaction: transaction);

            result.Success.Should().BeFalse();
            result.ErrorMessage.Should().Contain("missing and cannot be resumed");
            result.ErrorMessageKey.Should().Be("launcher.create.error.release");
            result.RecoveryPath.Should().Be(Path.GetFullPath(releaseDestination));
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
            result.Transaction.IsInterrupted.Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReportsWarningDuringVerificationStep()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var steps = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
            var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(step => steps.Add(step)),
                failStep: null);

            result.Success.Should().BeTrue();
            steps.Should().Contain(
                progress => progress.Step == CreatorStep.VerifyUsb && progress.Status == CreatorActionService.CreatorStepStatus.Warning);
            steps.Single(
                progress =>
                    progress.Step == CreatorStep.VerifyUsb &&
                    progress.Status == CreatorActionService.CreatorStepStatus.Warning)
                .MessageKey
                .Should().Be("launcher.create.warning.verify_usb");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReadsManifestAndWritesManifestMetadata()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "https://example.com/releases/system.img",
                  "Sha256": "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeTrue();

            var metadataPath = Path.Combine(path, "DEXTOR", "metadata", "device.json");
            File.Exists(metadataPath).Should().BeTrue();

            var metadata = JsonDocument.Parse(await File.ReadAllTextAsync(metadataPath));
            var root = metadata.RootElement;
            root.GetProperty("release_manifest_version").GetString().Should().Be("1.0.0");
            root.GetProperty("release_manifest_channel").GetString().Should().Be("beta");
            root.GetProperty("release_manifest_slots").GetInt32().Should().Be(1);
            root.GetProperty("release_manifest_artifacts").GetInt32().Should().Be(1);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnInvalidManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var manifest = $$"""
            {
              "Version": "",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "short"
                }
              ]
            }
            """;
        var steps = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(step => steps.Add(step)),
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_version_missing");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
            result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
            result.ErrorMessageArgs.Should().NotBeNull();
            result.ErrorMessageArgs.Should().ContainKey("error");
            result.ErrorMessageArgs!["error"].Should().Be("Release manifest version is missing.");
            steps.Should().Contain(
                progress => progress.Step == CreatorStep.PrepareRelease && progress.Status == CreatorActionService.CreatorStepStatus.Error);
            steps.First(
                progress => progress.Step == CreatorStep.PrepareRelease && progress.Status == CreatorActionService.CreatorStepStatus.Error)
                .MessageKey
                .Should().Be("launcher.create.error.release_manifest_version_missing");
            var invalidManifestProgress = steps.First(
                progress => progress.Step == CreatorStep.PrepareRelease && progress.Status == CreatorActionService.CreatorStepStatus.Error);
            invalidManifestProgress.MessageArgs.Should().NotBeNull();
            invalidManifestProgress.MessageArgs.Should().ContainKey("error");
            invalidManifestProgress.MessageArgs!["error"].Should().Be("Release manifest version is missing.");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnInvalidSlotHashInManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_slot_hash_invalid");
            result.ErrorMessage.Should().Contain("hash is invalid");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnMissingSlotImagePathInManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "https://example.com/releases/system.img",
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_slot_image_path_missing");
            result.ErrorMessage.Should().Contain("image path is missing");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnDuplicatedSlotNamesInManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                },
                {
                  "Name": "rootfs",
                  "ImagePath": "system-2.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "https://example.com/releases/system.img",
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_slot_duplicated");
            result.ErrorMessage.Should().Contain("is duplicated");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnInvalidArtifactHashInManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                }
              ],
              "Artifacts": [
                {
                  "Url": "https://example.com/releases/system.img",
                  "Sha256": "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_artifact_hash_invalid");
            result.ErrorMessage.Should().Contain("artifact");
            result.ErrorMessage.Should().Contain("hash is invalid");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnMissingArtifactUrlInManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_artifact_url_missing");
            result.ErrorMessage.Should().Contain("artifact url is missing");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnInvalidArtifactUrlInManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "ftp://example.com/releases/system.img",
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_artifact_url_invalid");
            result.ErrorMessage.Should().Contain("url is invalid");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

                if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsErrorOnNoArtifactsInManifest()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": []
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_no_artifacts");
            result.ErrorMessage.Should().Contain("contains no artifacts");
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsManifestErrorKeyAndRecoveryPathOnDuplicateSlots()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                },
                {
                  "Name": "rootfs",
                  "ImagePath": "system-2.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "https://example.com/releases/system.img",
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_slot_duplicated");
            result.ErrorMessageArgs.Should().NotBeNull();
            result.ErrorMessageArgs.Should().ContainKey("slot");
            result.ErrorMessageArgs["slot"].Should().Be("rootfs");
            result.RecoveryPath.Should().Be(Path.GetFullPath(manifestPath));
            result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncReturnsManifestErrorKeyAndRecoveryPathOnInvalidArtifactUrl()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var expectedSha = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);
        var invalidUrl = "ftp://example.com/releases/system.img";
        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "{{invalidUrl}}",
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                expectedSha,
                releaseDestination,
                null,
                failStep: null,
                releaseManifestPath: manifestPath);

            result.Success.Should().BeFalse();
            result.ErrorMessageKey.Should().Be("launcher.create.error.release_manifest_artifact_url_invalid");
            result.ErrorMessageArgs.Should().NotBeNull();
            result.ErrorMessageArgs.Should().ContainKey("url");
            result.ErrorMessageArgs["url"].Should().Be(invalidUrl);
            result.RecoveryPath.Should().Be(Path.GetFullPath(manifestPath));
            result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unchanged");
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public void RunCreateFlowCanResumeAfterSimulatedFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var firstAttempt = CreatorActionService.RunCreateFlow(
                path,
                new Progress<CreatorActionService.CreatorStepProgress>(),
                failStep: step => step == CreatorStep.WriteSystemImage,
                stepDelayMs: 0);

            firstAttempt.Success.Should().BeFalse();
            firstAttempt.Transaction.Current.Should().Be(CreatorStep.WriteSystemImage);
            firstAttempt.Transaction.IsInterrupted.Should().BeTrue();
            firstAttempt.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.possibly_modified");

            var loadedTransaction = CreatorActionService.TryLoadTransaction(path);
            loadedTransaction.Should().NotBeNull();

            var steps = new List<CreatorActionService.CreatorStepProgress>();
            var secondAttempt = CreatorActionService.RunCreateFlow(
                path,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => steps.Add(stepProgress)),
                failStep: null,
                stepDelayMs: 0,
                transaction: loadedTransaction);

            secondAttempt.Success.Should().BeTrue();
            secondAttempt.Transaction.IsInterrupted.Should().BeFalse();
            steps.Select(stepProgress => stepProgress.Step).Should().Equal(
                CreatorStep.WriteSystemImage,
                CreatorStep.CreateDataImage,
                CreatorStep.VerifyUsb,
                CreatorStep.ProvisionGuest,
                CreatorStep.Complete);
            CreatorActionService.HasSetupInterrupted(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Theory]
    [InlineData(CreatorStep.ConfirmErase)]
    [InlineData(CreatorStep.FormatUsb)]
    [InlineData(CreatorStep.WriteSystemImage)]
    [InlineData(CreatorStep.CreateDataImage)]
    [InlineData(CreatorStep.VerifyUsb)]
    [InlineData(CreatorStep.ProvisionGuest)]
    public void RunCreateFlowCanResumeFromSimulatedFailureAtAnyStep(CreatorStep failedStep)
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var failedSecondAttempt = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            var firstAttempt = CreatorActionService.RunCreateFlow(
                path,
                new Progress<CreatorActionService.CreatorStepProgress>(),
                failStep: step => step == failedStep,
                stepDelayMs: 0);

            firstAttempt.Success.Should().BeFalse();
            firstAttempt.Transaction.IsInterrupted.Should().BeTrue();
            var savedTransaction = CreatorActionService.TryLoadTransaction(path);
            savedTransaction.Should().NotBeNull();

            var secondAttempt = CreatorActionService.RunCreateFlow(
                path,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => failedSecondAttempt.Add(stepProgress)),
                failStep: null,
                stepDelayMs: 0,
                transaction: savedTransaction);

            secondAttempt.Success.Should().BeTrue();
            failedSecondAttempt.First().Step.Should().Be(failedStep);
            failedSecondAttempt.Last().Step.Should().Be(CreatorStep.Complete);
            failedSecondAttempt.Should().NotContain(progress => progress.Step == CreatorStep.PrepareRelease);
            CreatorActionService.TryLoadTransaction(path).Should().BeNull();
            CreatorActionService.HasSetupInterrupted(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void RunCreateFlowCanResumeFromInterruptedTransaction()
    {
        var transaction = CreatorActionService.CreateTransaction();
        transaction.MarkCompleted(CreatorStep.PrepareRelease);
        transaction.MarkCompleted(CreatorStep.ConfirmErase);
        transaction.Interrupt();

        var steps = new List<CreatorActionService.CreatorStepProgress>();
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var result = CreatorActionService.RunCreateFlow(
                path,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => steps.Add(stepProgress)),
                failStep: null,
                stepDelayMs: 0,
                transaction: transaction);

            result.Success.Should().BeTrue();
            result.Transaction.IsInterrupted.Should().BeFalse();
            steps.Select(stepProgress => stepProgress.Step).Should().Equal(
                CreatorStep.FormatUsb,
                CreatorStep.CreateDataImage,
                CreatorStep.VerifyUsb,
                CreatorStep.ProvisionGuest,
                CreatorStep.Complete);
            steps.Should().NotContain(progress => progress.Step == CreatorStep.PrepareRelease);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncCanResumeWithoutRepreparing()
    {
        var transaction = CreatorActionService.CreateTransaction();
        transaction.MarkCompleted(CreatorStep.PrepareRelease);
        transaction.MarkCompleted(CreatorStep.ConfirmErase);
        transaction.Interrupt();

        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var manifestHash = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);

        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "https://example.com/releases/system.img",
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        var steps = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(releaseDestination)!);
            await File.WriteAllBytesAsync(releaseDestination, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                manifestHash,
                releaseDestination,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => steps.Add(stepProgress)),
                failStep: step => step == CreatorStep.PrepareRelease,
                stepDelayMs: 0,
                releaseManifestPath: manifestPath,
                transaction: transaction);

            result.Success.Should().BeTrue();
            result.Transaction.Current.Should().Be(CreatorStep.Complete);
            CreatorActionService.TryLoadTransaction(path).Should().BeNull();
            steps.Select(stepProgress => stepProgress.Step).Should().Equal(
                CreatorStep.FormatUsb,
                CreatorStep.CreateDataImage,
                CreatorStep.VerifyUsb,
                CreatorStep.ProvisionGuest,
                CreatorStep.Complete);
            steps.Should().NotContain(progress => progress.Step == CreatorStep.PrepareRelease);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public async Task RunCreateFlowWithReleaseAsyncCanResumeFromTransactionLoadedFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var releaseSourceDir = Path.Combine(Path.GetTempPath(), "dextor_release_source", Path.GetRandomFileName());
        var releaseSource = Path.Combine(releaseSourceDir, "system.img");
        var releaseDestination = Path.Combine(releaseSourceDir, "cache", "system.img");
        var manifestPath = Path.Combine(releaseSourceDir, "release-manifest.json");
        var payloadBytes = Encoding.UTF8.GetBytes("dextor-release-payload");
        var manifestHash = ReleaseIntegrity.ComputeSha256(payloadBytes);
        var validHash = new string('a', 64);

        var manifest = $$"""
            {
              "Version": "1.0.0",
              "Channel": "beta",
              "Slots": [
                {
                  "Name": "rootfs",
                  "ImagePath": "system.img",
                  "Hash": "{{validHash}}"
                }
              ],
              "Artifacts": [
                {
                  "Url": "https://example.com/releases/system.img",
                  "Sha256": "{{validHash}}",
                  "FileName": "system.img"
                }
              ]
            }
            """;

        var steps = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            Directory.CreateDirectory(releaseSourceDir);
            await File.WriteAllBytesAsync(releaseSource, payloadBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(releaseDestination)!);
            await File.WriteAllBytesAsync(releaseDestination, payloadBytes);
            await File.WriteAllTextAsync(manifestPath, manifest);

            var sourceTransaction = CreatorActionService.CreateTransaction();
            sourceTransaction.MarkCompleted(CreatorStep.PrepareRelease);
            sourceTransaction.MarkCompleted(CreatorStep.ConfirmErase);
            sourceTransaction.Interrupt();
            CreatorActionService.SaveTransactionState(path, sourceTransaction).Should().BeTrue();

            var loadedTransaction = CreatorActionService.TryLoadTransaction(path);
            loadedTransaction.Should().NotBeNull();
            loadedTransaction!.IsInterrupted.Should().BeTrue();

            var result = await CreatorActionService.RunCreateFlowWithReleaseAsync(
                path,
                releaseSource,
                manifestHash,
                releaseDestination,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => steps.Add(stepProgress)),
                failStep: step => step == CreatorStep.PrepareRelease,
                stepDelayMs: 0,
                releaseManifestPath: manifestPath,
                transaction: loadedTransaction);

            result.Success.Should().BeTrue();
            result.Transaction.Current.Should().Be(CreatorStep.Complete);
            result.Transaction.IsInterrupted.Should().BeFalse();
            CreatorActionService.TryLoadTransaction(path).Should().BeNull();
            steps.Select(stepProgress => stepProgress.Step).Should().Equal(
                CreatorStep.FormatUsb,
                CreatorStep.CreateDataImage,
                CreatorStep.VerifyUsb,
                CreatorStep.ProvisionGuest,
                CreatorStep.Complete);
            steps.Should().NotContain(progress => progress.Step == CreatorStep.PrepareRelease);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }

            if (Directory.Exists(releaseSourceDir))
            {
                Directory.Delete(releaseSourceDir, true);
            }
        }
    }

    [Fact]
    public void TryLoadTransactionReturnsSavedTransactionState()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var transaction = CreatorActionService.CreateTransaction();
            transaction.MarkCompleted(CreatorStep.PrepareRelease);
            transaction.MarkCompleted(CreatorStep.ConfirmErase);
            transaction.Interrupt();

            CreatorActionService.SaveTransactionState(path, transaction).Should().BeTrue();

            var loaded = CreatorActionService.TryLoadTransaction(path);
            loaded.Should().NotBeNull();
            loaded!.LastCompleted.Should().Be(CreatorStep.ConfirmErase);
            loaded.IsInterrupted.Should().BeTrue();
            loaded.Current.Should().Be(CreatorStep.FormatUsb);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void HasSetupInterruptedReturnsFalseWhenNoTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            CreatorActionService.HasSetupInterrupted(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void HasSetupInterruptedReturnsFalseForCompletedTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var transaction = CreatorActionService.CreateTransaction();
            transaction.MarkCompleted(CreatorStep.PrepareRelease);
            transaction.MarkCompleted(CreatorStep.ConfirmErase);
            transaction.MarkCompleted(CreatorStep.FormatUsb);
            transaction.MarkCompleted(CreatorStep.WriteSystemImage);
            transaction.MarkCompleted(CreatorStep.CreateDataImage);
            transaction.MarkCompleted(CreatorStep.VerifyUsb);
            transaction.MarkCompleted(CreatorStep.ProvisionGuest);
            transaction.MarkCompleted(CreatorStep.Complete);

            CreatorActionService.SaveTransactionState(path, transaction).Should().BeTrue();

            CreatorActionService.HasSetupInterrupted(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void HasSetupInterruptedReturnsTrueForInterruptedTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var transaction = CreatorActionService.CreateTransaction();
            transaction.MarkCompleted(CreatorStep.PrepareRelease);
            transaction.MarkCompleted(CreatorStep.ConfirmErase);
            transaction.Interrupt();

            CreatorActionService.SaveTransactionState(path, transaction).Should().BeTrue();

            CreatorActionService.HasSetupInterrupted(path).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void HasSetupInterruptedReturnsTrueForIncompleteUninterruptedTransaction()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var transaction = CreatorActionService.CreateTransaction();
            transaction.MarkCompleted(CreatorStep.PrepareRelease);
            transaction.MarkCompleted(CreatorStep.ConfirmErase);

            CreatorActionService.SaveTransactionState(path, transaction).Should().BeTrue();

            CreatorActionService.HasSetupInterrupted(path).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void HasSetupInterruptedReturnsFalseForCorruptedTransactionState()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var transactionStatePath = CreatorActionService.GetTransactionStatePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(transactionStatePath)!);
            File.WriteAllText(transactionStatePath, "{ this is not valid json }");

            CreatorActionService.HasSetupInterrupted(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Fact]
    public void HasSetupInterruptedReturnsFalseForEmptyPath()
    {
        CreatorActionService.HasSetupInterrupted(string.Empty).Should().BeFalse();
    }

    [Fact]
    public void HasSetupInterruptedReturnsFalseForInvalidTransactionStateStep()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());

        try
        {
            var transactionStatePath = CreatorActionService.GetTransactionStatePath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(transactionStatePath)!);
            File.WriteAllText(
                transactionStatePath,
                """{"Id":"00000000-0000-0000-0000-000000000000","LastCompleted":"NotARealStep","IsInterrupted":true}""");

            CreatorActionService.HasSetupInterrupted(path).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    [Theory]
    [InlineData(null, "launcher.action.create.solution")]
    [InlineData("", "launcher.action.create.solution")]
    [InlineData("   ", "launcher.action.create.solution")]
    [InlineData("launcher.create.error.release_manifest_version_missing", "launcher.action.create.solution.release_manifest")]
    [InlineData("launcher.create.error.release_manifest_artifact_hash_invalid", "launcher.action.create.solution.release_manifest")]
    [InlineData("launcher.create.error.release_config", "launcher.action.create.solution.release_config")]
    [InlineData("launcher.create.error.release", "launcher.action.create.solution.release")]
    [InlineData("launcher.create.error.simulation", "launcher.action.create.solution")]
    public void GetCreateErrorSolutionKeyMapsErrorKeys(
        string? errorMessageKey,
        string expectedSolutionKey)
    {
        CreatorActionService.GetCreateErrorSolutionKey(errorMessageKey).Should().Be(expectedSolutionKey);
    }

    [Theory]
    [InlineData(null, "launcher.action.create.warning.solution")]
    [InlineData("", "launcher.action.create.warning.solution")]
    [InlineData("   ", "launcher.action.create.warning.solution")]
    [InlineData("launcher.create.warning.verify_usb", "launcher.action.create.warning.solution.verify_usb")]
    [InlineData("launcher.create.warning.unknown", "launcher.action.create.warning.solution")]
    public void GetCreateWarningSolutionKeyMapsWarningKeys(
        string? warningMessageKey,
        string expectedSolutionKey)
    {
        CreatorActionService.GetCreateWarningSolutionKey(warningMessageKey).Should().Be(expectedSolutionKey);
    }

    [Theory]
    [InlineData(null, null, "launcher.create.error.safety.unknown")]
    [InlineData("", CreatorStep.PrepareRelease, "launcher.create.error.safety.unchanged")]
    [InlineData("launcher.create.error.release", CreatorStep.PrepareRelease, "launcher.create.error.safety.unchanged")]
    [InlineData("launcher.create.error.simulation", CreatorStep.VerifyUsb, "launcher.create.error.safety.possibly_modified")]
    [InlineData("launcher.create.error.simulation", null, "launcher.create.error.safety.unknown")]
    public void GetCreateErrorSafetyKeyMapsErrorStepsAndMessages(
        string? messageKey,
        CreatorStep? errorStep,
        string expectedSafetyKey)
    {
        CreatorActionService.GetCreateErrorSafetyKey(
                step: errorStep,
                errorMessageKey: messageKey)
            .Should().Be(expectedSafetyKey);
    }

    [Fact]
    public void RunCreateFlowCanResumeFromTransactionLoadedFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_creator_test", Path.GetRandomFileName());
        var steps = new List<CreatorActionService.CreatorStepProgress>();

        try
        {
            var sourceTransaction = CreatorActionService.CreateTransaction();
            sourceTransaction.MarkCompleted(CreatorStep.PrepareRelease);
            sourceTransaction.MarkCompleted(CreatorStep.ConfirmErase);
            sourceTransaction.Interrupt();
            CreatorActionService.SaveTransactionState(path, sourceTransaction).Should().BeTrue();

            var loadedTransaction = CreatorActionService.TryLoadTransaction(path);
            loadedTransaction.Should().NotBeNull();

            var result = CreatorActionService.RunCreateFlow(
                path,
                new SynchronousProgress<CreatorActionService.CreatorStepProgress>(stepProgress => steps.Add(stepProgress)),
                failStep: null,
                stepDelayMs: 0,
                transaction: loadedTransaction!);

            result.Success.Should().BeTrue();
            result.Transaction.Current.Should().Be(CreatorStep.Complete);
            result.Transaction.IsInterrupted.Should().BeFalse();
            CreatorActionService.TryLoadTransaction(path).Should().BeNull();
            steps.Select(stepProgress => stepProgress.Step).Should().Equal(
                CreatorStep.FormatUsb,
                CreatorStep.CreateDataImage,
                CreatorStep.VerifyUsb,
                CreatorStep.ProvisionGuest,
                CreatorStep.Complete);
            steps.Should().NotContain(progress => progress.Step == CreatorStep.PrepareRelease);
        }
        finally
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
    }

    private sealed class FailingReleaseDownloader : IReleaseDownloader
    {
        private readonly string _errorMessage;

        public FailingReleaseDownloader(string errorMessage)
        {
            _errorMessage = errorMessage;
        }

        public Task<ReleaseDownloadResult> DownloadAsync(
            string source,
            string destinationPath,
            string expectedSha256,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ReleaseDownloadResult(false, destinationPath, _errorMessage));
        }
    }
}

