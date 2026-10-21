using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Contracts;
using FluentAssertions;
using Xunit;

namespace DexTor.Qemu.Tests;

public sealed class VmPrelaunchGateTests : IDisposable
{
    private static readonly string[] RuntimeFiles = ["qemu-system-x86_64.exe", "OVMF.fd", "edk2-x64"];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-qemu-tests-" + Guid.NewGuid().ToString("N"));

    public VmPrelaunchGateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ResolveSystemImagePrefersVerifiedTemporaryCopy()
    {
        var preferred = WriteFile("cached.iso", "verified-image");
        var fallback = WriteFile("usb.img", "verified-image");
        var request = CreateRequest(preferred, fallback, Sha256(preferred));

        var result = VmPrelaunchGate.ResolveSystemImage(request);

        result.Path.Should().Be(Path.GetFullPath(preferred));
        result.Location.Should().Be(SystemImageLocation.TemporaryCache);
    }

    [Fact]
    public void ResolveSystemImageUsesUsbWhenTemporaryCopyIsCorrupt()
    {
        var preferred = WriteFile("cached.iso", "corrupt");
        var fallback = WriteFile("usb.img", "verified-image");
        var request = CreateRequest(preferred, fallback, Sha256(fallback));

        var result = VmPrelaunchGate.ResolveSystemImage(request);

        result.Path.Should().Be(Path.GetFullPath(fallback));
        result.Location.Should().Be(SystemImageLocation.UsbFallback);
    }

    [Fact]
    public void ResolveSystemImageRejectsWhenNeitherCopyMatches()
    {
        var preferred = WriteFile("cached.iso", "corrupt-a");
        var fallback = WriteFile("usb.img", "corrupt-b");
        var request = CreateRequest(preferred, fallback, new string('a', 64));

        var action = () => VmPrelaunchGate.ResolveSystemImage(request);

        action.Should().Throw<VmPrelaunchException>();
    }

    [Fact]
    public void AcquireDataImageLeasePreventsASecondWriter()
    {
        var dataImage = WriteFile("data.img", "persistent-data");

        using var lease = VmPrelaunchGate.AcquireDataImageLease(dataImage);
        var action = () => VmPrelaunchGate.AcquireDataImageLease(dataImage);

        action.Should().Throw<VmPrelaunchException>();
    }

    [Fact]
    public void ValidateRuntimeAcceptsOnlyACompleteContainedBundle()
    {
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "edk2-x64"));
        File.WriteAllBytes(Path.Combine(runtime, "qemu-system-x86_64.exe"), []);
        File.WriteAllBytes(Path.Combine(runtime, "OVMF.fd"), []);
        var manifest = Path.Combine(runtime, "qemu-manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            runtime = new { files = RuntimeFiles }
        }));

        var executable = VmPrelaunchGate.ValidateRuntime(runtime, manifest);

        executable.Should().Be(Path.Combine(Path.GetFullPath(runtime), "qemu-system-x86_64.exe"));
    }

    [Fact]
    public void ValidateRuntimeAcceptsGeneratedInstallerPayloadManifest()
    {
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(Path.Combine(runtime, "share"));
        File.WriteAllBytes(Path.Combine(runtime, "qemu-system-x86_64.exe"), []);
        File.WriteAllBytes(Path.Combine(runtime, "dependency.dll"), []);
        var manifest = Path.Combine(runtime, "qemu-runtime-files.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            version = "11.1.0",
            files = new[]
            {
                new { path = "qemu-system-x86_64.exe" },
                new { path = "dependency.dll" }
            }
        }));

        var executable = VmPrelaunchGate.ValidateRuntime(runtime, manifest);

        executable.Should().Be(Path.Combine(Path.GetFullPath(runtime), "qemu-system-x86_64.exe"));
    }

    [Fact]
    public void ValidateRuntimeRejectsPathsOutsideBundle()
    {
        var runtime = Path.Combine(_root, "runtime");
        Directory.CreateDirectory(runtime);
        var manifest = Path.Combine(runtime, "qemu-manifest.json");
        File.WriteAllText(manifest, "{\"runtime\":{\"files\":[\"..\\\\outside.exe\"]}}");

        var action = () => VmPrelaunchGate.ValidateRuntime(runtime, manifest);

        action.Should().Throw<VmPrelaunchException>();
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    private SessionStartRequest CreateRequest(string preferred, string fallback, string hash) =>
        new(Guid.NewGuid(), preferred, fallback, Path.Combine(_root, "data.img"), hash, 4096, 4);

    private string WriteFile(string name, string contents)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
