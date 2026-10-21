using System.Collections.Generic;
using System.Windows.Forms;

using DexTor.Application;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class LiveImageSetupWindowTests
{
    [Fact]
    public async Task MissingCacheShowsDownloadActionOnSecondScreen()
    {
        using var window = CreateWindow(new LiveImageCacheInspectionResult(
            true,
            false,
            @"C:\Temp\DexTor\LiveImage",
            "https://example.test/latest.iso",
            @"C:\Temp\DexTor\LiveImage\latest.iso",
            null));
        window.CreateControl();

        await window.InspectAsync();

        window.Controls.Find("ImageSetupHeadline", true).Single().Text.Should().Be("launcher.image_setup.download_required");
        var primary = window.Controls.Find("ImageSetupPrimaryButton", true).Single().Should().BeOfType<Button>().Subject;
        primary.Text.Should().Be("launcher.image_setup.download");
        primary.Enabled.Should().BeTrue();
        window.Controls.Find("ImageSetupDetail", true).Single().Text.Should().BeEmpty();
        window.Size.Should().Be(new System.Drawing.Size(940, 740));
        window.Controls.Find("ImageSetupSpinner", true).Should().BeEmpty();
        window.Controls.Find("DexTorBrandFooter", true).Should().ContainSingle();
        var root = window.Controls.Find("ImageSetupRoot", true).Should().ContainSingle().Subject;
        var footer = window.Controls.Find("DexTorBrandFooter", true).Single();
        root.Bottom.Should().BeLessThanOrEqualTo(footer.Top);
        var site = window.Controls.Find("DexTorBrandSiteLink", true).Should().ContainSingle().Subject;
        site.Tag.Should().Be(LauncherBranding.SiteUrl);
    }

    [Fact]
    public async Task VerifiedCacheShowsContinueAndUsbInstruction()
    {
        using var window = CreateWindow(new LiveImageCacheInspectionResult(
            true,
            true,
            @"C:\Temp\DexTor\LiveImage",
            "https://example.test/latest.iso",
            @"C:\Temp\DexTor\LiveImage\latest.iso",
            "abc123",
            1024));
        window.CreateControl();

        await window.InspectAsync();

        window.PreparedImage.Should().NotBeNull();
        window.Controls.Find("ImageSetupHeadline", true).Single().Text.Should().Be("launcher.image_setup.ready");
        window.Controls.Find("ImageSetupDescription", true).Single().Text.Should().Be("launcher.image_setup.ready_details");
        var primary = window.Controls.Find("ImageSetupPrimaryButton", true).Single().Should().BeOfType<Button>().Subject;
        primary.Text.Should().Be("launcher.primary_action_continue");
        primary.Enabled.Should().BeTrue();
    }

    private static LiveImageSetupWindow CreateWindow(LiveImageCacheInspectionResult inspection)
    {
        return new LiveImageSetupWindow(
            new TestLocalizer(),
            (_, _) => Task.FromResult(inspection),
            (_, _) => Task.FromResult(new LiveImagePreparationResult(false, null, null, null)),
            () => false);
    }

    private sealed class TestLocalizer : ILocalizer
    {
        public string T(string key, IReadOnlyDictionary<string, string>? args = null) => key;

        public bool TryGetLanguage(out string language)
        {
            language = "en";
            return true;
        }
    }
}
