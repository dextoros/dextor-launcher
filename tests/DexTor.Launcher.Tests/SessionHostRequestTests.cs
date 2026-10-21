using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Qemu;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class SessionHostRequestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-session-request-" + Guid.NewGuid().ToString("N"));

    public SessionHostRequestTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void BuildStartRequestReadsOnlyVerifiedPayloadPathsInsideUsb()
    {
        var system = CreatePayload("DEXTOR/system/slot-a/system.img", "system");
        var data = CreatePayload("DEXTOR/storage/data.img", "data");
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(system)));
        WriteMetadata(new
        {
            state = "ready",
            systemImageSha256 = hash,
            systemImagePath = @"DEXTOR\system\slot-a\system.img",
            dataImagePath = @"DEXTOR\storage\data.img"
        });

        var request = SessionHostService.BuildStartRequest(_root);

        request.UsbSystemImagePath.Should().Be(system);
        request.DataImagePath.Should().Be(data);
        request.ExpectedSystemImageSha256.Should().Be(hash);
        request.MemoryMiB.Should().Be(VmResources.ForThisComputer().MemoryMiB);
    }

    [Fact]
    public void BuildStartRequestRejectsPayloadPathOutsideUsb()
    {
        WriteMetadata(new
        {
            state = "ready",
            systemImageSha256 = new string('a', 64),
            systemImagePath = @"..\outside.img",
            dataImagePath = @"DEXTOR\storage\data.img"
        });

        var action = () => SessionHostService.BuildStartRequest(_root);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TheCommandLineStartsWithTheExecutableAndKeepsEveryArgument()
    {
        var plan = new QemuLaunchPlan(@"C:\QEMU\qemu-system-x86_64.exe", ["-display", "sdl,gl=off"]);

        var commandLine = QemuProcessLauncher.BuildCommandLine(plan).TrimEnd('\0');

        // argv[0] is the executable, as every Windows command line has it; the real
        // binary comes from lpApplicationName, so this is only what QEMU reads back.
        commandLine.Should().Be("""
            "C:\QEMU\qemu-system-x86_64.exe" "-display" "sdl,gl=off"
            """);
        commandLine.Should().NotContain("-full-screen");
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("has space", "\"has space\"")]
    [InlineData(@"trailing\", @"""trailing\\""")]
    [InlineData(@"two\\", @"""two\\\\""")]
    [InlineData("quo\"te", "\"quo\\\"te\"")]
    [InlineData(@"path\with space\", @"""path\with space\\""")]
    [InlineData("юникод", "\"юникод\"")]
    public void ArgumentBoundariesSurviveQuoting(string argument, string expected)
    {
        // The CRT's documented rules, which is what ProcessStartInfo.ArgumentList applied
        // before the launcher built the command line itself.
        var plan = new QemuLaunchPlan(@"C:\QEMU\qemu-system-x86_64.exe", [argument]);

        var commandLine = QemuProcessLauncher.BuildCommandLine(plan).TrimEnd('\0');

        commandLine.Should().EndWith(" " + expected);
    }

    [Fact]
    public async Task EmergencyStopPersistsAnUncleanUsbRemovalJournal()
    {
        var journalPath = Path.Combine(_root, "session.json");
        await using var service = new SessionHostService(journalPath: journalPath);

        await service.EmergencyStopAsync();

        service.State.Should().Be(SessionState.SafeToEject);
        service.LastError.Should().Contain("USB drive was removed");
        using var journal = JsonDocument.Parse(File.ReadAllText(journalPath));
        journal.RootElement.GetProperty("state").GetString().Should().Be("emergency-usb-removed");
        journal.RootElement.GetProperty("clean").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void DefaultRuntimeRootPrefersInstalledBundledRuntime()
    {
        var applicationRoot = Path.Combine(_root, "Application");
        var temporaryRoot = Path.Combine(_root, "Temp");
        var bundledRoot = Path.Combine(applicationRoot, "runtime", "qemu");
        Directory.CreateDirectory(Path.Combine(bundledRoot, "share"));
        File.WriteAllBytes(Path.Combine(bundledRoot, "qemu-system-x86_64.exe"), []);

        var result = SessionHostService.ResolveDefaultRuntimeRoot(applicationRoot, temporaryRoot);

        result.Should().Be(bundledRoot);
    }

    [Fact]
    public void DefaultRuntimeRootFallsBackToVersionedTemporaryRuntime()
    {
        var applicationRoot = Path.Combine(_root, "Application");
        var temporaryRoot = Path.Combine(_root, "Temp");

        var result = SessionHostService.ResolveDefaultRuntimeRoot(applicationRoot, temporaryRoot);

        result.Should().Be(Path.Combine(temporaryRoot, "qemu-11.1.0"));
    }

    [Fact]
    public void DefaultRuntimeManifestPrefersManifestInsideBundledRuntime()
    {
        var applicationRoot = Path.Combine(_root, "Application");
        var runtimeRoot = Path.Combine(applicationRoot, "runtime", "qemu");
        Directory.CreateDirectory(runtimeRoot);
        var payloadManifest = Path.Combine(runtimeRoot, "qemu-runtime-files.json");
        File.WriteAllText(payloadManifest, "{}");

        var result = SessionHostService.ResolveDefaultRuntimeManifestPath(runtimeRoot, applicationRoot);

        result.Should().Be(payloadManifest);
    }

    [Fact]
    public void QemuDiagnosticsKeepTheUsefulLastErrorLine()
    {
        var result = SessionHostService.FormatQemuDiagnostics(
            "qemu: warning: harmless warning\r\nqemu: failed to open the system image");

        result.Should().Be("qemu: failed to open the system image");
    }

    [Fact]
    public void TheCpuidWarningQemuPrintsOnEveryStartIsNotTakenForTheReasonItStopped()
    {
        // Printed under WHPX on every start; it used to be reported as "the CPU profile is
        // not supported" after the guest had simply been shut down.
        SessionHostService.FormatQemuDiagnostics(
            "qemu-system-x86_64.exe: warning: host doesn't support requested feature: CPUID[eax=80000001h].ECX.svm [bit 2]")
            .Should().BeNull();
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string CreatePayload(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private void WriteMetadata(object metadata)
    {
        var path = Path.Combine(_root, "DEXTOR", "metadata", "device.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(metadata));
    }

    [Theory]
    [InlineData("en", "en_US.UTF-8")]
    [InlineData("ru", "ru_RU.UTF-8")]
    [InlineData("uk", "uk_UA.UTF-8")]
    [InlineData("de", "de_DE.UTF-8")]
    [InlineData("es", "es_ES.UTF-8")]
    [InlineData("fr", "fr_FR.UTF-8")]
    [InlineData("pt-BR", "pt_BR.UTF-8")]
    [InlineData("pl", "pl_PL.UTF-8")]
    [InlineData("tr", "tr_TR.UTF-8")]
    [InlineData("zh-CN", "zh_CN.UTF-8")]
    [InlineData("ja", null)]
    [InlineData(null, null)]
    public void TheWizardsLanguageBecomesTheGuestsLocale(string? language, string? expected)
    {
        SessionHostService.ToGuestLocale(language).Should().Be(expected);
    }

    [Fact]
    public void EveryLanguageOfTheWizardReachesTheGuest()
    {
        foreach (var language in DexTor.Application.LocaleCatalog.SupportedLanguages)
        {
            SessionHostService.ToGuestLocale(language).Should().NotBeNull($"the wizard offers {language}");
        }
    }

    [Fact]
    public void ABuildThatDoesNotSayReleaseIsATestBuild()
    {
        DexTor.Contracts.DexTorBuildInfo.Channel.Should().Be(DexTor.Contracts.DexTorBuildInfo.TestChannel);
    }

    [Theory]
    [InlineData(64L, 16, 8192, 8)]
    [InlineData(32L, 12, 8192, 6)]
    [InlineData(16L, 8, 6144, 4)]
    [InlineData(24L, 4, 8192, 2)]
    [InlineData(8L, 4, 4096, 2)]
    [InlineData(6L, 2, 3072, 2)]
    public void TheVmIsSizedToTheComputerItRunsOn(long gigabytes, int logical, int expectedMemoryMiB, int expectedProcessors)
    {
        VmResources.ForHost(gigabytes * 1024 * 1024 * 1024, logical).Should().Be((expectedMemoryMiB, expectedProcessors));
    }
}
