using FluentAssertions;

using Xunit;

namespace DexTor.Contracts.Tests;

/// <summary>
/// Where the data lives when the launcher runs from its Store package, and how the
/// package is recognised.
/// </summary>
public sealed class PackagedStorageTests
{
    private const string LocalAppData = @"C:\Users\someone\AppData\Local";

    [Fact]
    public void UnpackagedLauncherKeepsItsDataInLocalAppDataDexTor()
    {
        DexTorStorage.ResolveRoot(LocalAppData, null)
            .Should().Be(Path.Combine(LocalAppData, "DexTor"));
    }

    [Fact]
    public void PackagedLauncherNamesTheFolderWindowsRedirectsItsLocalAppDataTo()
    {
        // The real path behind the packaged app's %LOCALAPPDATA%\DexTor, so the elevated
        // provisioner - which may run without the package identity - finds the same files.
        DexTorStorage.ResolveRoot(LocalAppData, "DexTorOS.DexTorOS_abcdefghjkmnp")
            .Should().Be(Path.Combine(LocalAppData, "Packages", "DexTorOS.DexTorOS_abcdefghjkmnp", "LocalCache", "Local", "DexTor"));
    }

    [Fact]
    public void TestProcessIsNotPackaged()
    {
        PackagedApp.FamilyName.Should().BeNull("the test host is no MSIX package and has no AppxManifest.xml beside it");
        PackagedApp.IsPackaged.Should().BeFalse();
    }

    [Fact]
    public void FamilyNameComesFromWindowsOwnMapping()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // A well-known package: Windows Calculator's family name is published everywhere.
        PackagedApp.FamilyNameFromId(
                "Microsoft.WindowsCalculator",
                "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US")
            .Should().Be("Microsoft.WindowsCalculator_8wekyb3d8bbwe");
    }

    [Fact]
    public void FamilyNameIsReadFromTheManifestBesideTheExecutable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "DexTorTests", "Manifest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = Path.Combine(directory, "AppxManifest.xml");
            File.WriteAllText(manifest, """
                <?xml version="1.0" encoding="utf-8"?>
                <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
                  <Identity Name="Microsoft.WindowsCalculator" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="1.0.0.0" ProcessorArchitecture="x64" />
                </Package>
                """);

            PackagedApp.FamilyNameFromManifest(manifest).Should().Be("Microsoft.WindowsCalculator_8wekyb3d8bbwe");
            PackagedApp.FamilyNameFromManifest(Path.Combine(directory, "missing.xml")).Should().BeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
