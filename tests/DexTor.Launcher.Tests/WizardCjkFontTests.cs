using System.Drawing;
using DexTor.Application;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class WizardCjkFontTests
{
    [Fact]
    public void ChineseIsDrawnInACjkFaceAndEveryOtherLanguageInInter()
    {
        try
        {
            WizardFonts.UseLanguage("zh-CN").Should().BeTrue();
            using (var font = WizardFonts.Get(14, 600))
            {
                // Inter has no CJK glyphs; the ones Windows put in their place overlapped.
                font.FontFamily.Name.Should().Be(WizardFonts.CjkFamilyName);
                font.Bold.Should().BeTrue();
            }

            WizardFonts.UseLanguage("ru").Should().BeTrue();
            using var russian = WizardFonts.Get(14, 600);
            russian.FontFamily.Name.Should().NotBe(WizardFonts.CjkFamilyName);
        }
        finally
        {
            WizardFonts.UseLanguage("en");
        }
    }

    [Fact]
    public void SwitchingToChineseRestylesTheWizardThatIsAlreadyOpen()
    {
        try
        {
            WizardFonts.UseLanguage("en");
            using var window = new LauncherWizardWindow(new Localizer("en"), WizardScreenshots.Dependencies.ForTests());
            window.CreateControl();

            typeof(LauncherWizardWindow).GetMethod("ChangeLanguage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, ["zh-CN"]);

            window.Font.FontFamily.Name.Should().Be(WizardFonts.CjkFamilyName);
            AllControls(window).Where(control => control.Font.FontFamily.Name.StartsWith("Inter", StringComparison.Ordinal))
                .Should().BeEmpty("no control keeps a face without Chinese glyphs");
        }
        finally
        {
            WizardFonts.UseLanguage("en");
        }
    }

    private static IEnumerable<System.Windows.Forms.Control> AllControls(System.Windows.Forms.Control root) =>
        root.Controls.Cast<System.Windows.Forms.Control>().SelectMany(child => AllControls(child).Prepend(child));

    [Theory]
    [InlineData(1.0f, 1366, 728, 1.0f)]
    [InlineData(1.5f, 1920, 1032, 1.474f)]
    [InlineData(1.5f, 1366, 728, 1.04f)]
    [InlineData(2.0f, 3840, 2088, 2.0f)]
    [InlineData(1.25f, 2560, 1400, 1.25f)]
    public void TheWizardIsScaledNoLargerThanFitsTheScreen(float dpiScale, int workWidth, int workHeight, float expected)
    {
        // 960 x 648 plus the title bar and a margin must fit the work area.
        var scale = WizardDesign.FitScale(dpiScale, workWidth, workHeight);
        scale.Should().BeApproximately(expected, 0.01f);
        (980 * scale).Should().BeLessThanOrEqualTo(workWidth + 0.5f);
        (700 * scale).Should().BeLessThanOrEqualTo(workHeight + 0.5f);
    }
}
