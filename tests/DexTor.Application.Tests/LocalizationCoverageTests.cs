using FluentAssertions;
using Xunit;

using DexTor.Application;

namespace DexTor.Application.Tests;

public partial class LocalizationCoverageTests
{
    [Fact]
    public void AllSupportedLanguagesContainSameLocalizationKeys()
    {
        var baseLanguage = LocaleCatalog.GetLocalizationKeys(Localizer.DefaultLanguage);
        foreach (var language in new[] { "ru", "uk" })
        {
            var keys = LocaleCatalog.GetLocalizationKeys(language);
            keys.Should().BeEquivalentTo(baseLanguage, $"language '{language}' must have all base keys in sync with {Localizer.DefaultLanguage}");
        }
    }

    [Theory]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("pt-BR")]
    [InlineData("pl")]
    [InlineData("tr")]
    [InlineData("zh-CN")]
    public void EveryOtherLanguageTranslatesEveryStringWithTheSamePlaceholders(string language)
    {
        // Only step names and headings used to be translated here; the pages showed English.
        var keys = LocaleCatalog.GetLocalizationKeys(language);
        foreach (var key in LocaleCatalog.GetLocalizationKeys(Localizer.DefaultLanguage))
        {
            keys.Should().Contain(key, $"'{language}' must translate {key}");
            var english = new Localizer(Localizer.DefaultLanguage).T(key);
            var translated = new Localizer(language).T(key);
            Placeholders(translated).Should().BeEquivalentTo(Placeholders(english), $"{language}: {key}");
        }
    }

    private static string[] Placeholders(string text) =>
        PlaceholderPattern().Matches(text).Select(match => match.Value).Distinct().ToArray();

    [System.Text.RegularExpressions.GeneratedRegex(@"\{[a-z_]+\}")]
    private static partial System.Text.RegularExpressions.Regex PlaceholderPattern();

    [Fact]
    public void FallbackLanguageIsEnglish()
    {
        LocaleCatalog.GetSupportedLanguage("ja-JP").Should().Be(Localizer.DefaultLanguage);
    }

    [Fact]
    public void LanguageCanBeChangedAtRuntime()
    {
        var localizer = new Localizer("en");

        localizer.SetLanguage("ru");

        localizer.T("launcher.wizard.back").Should().Be("Назад");
    }

    [Theory]
    [InlineData("de-DE", "de")]
    [InlineData("es-MX", "es")]
    [InlineData("fr-CA", "fr")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pl-PL", "pl")]
    [InlineData("tr-TR", "tr")]
    [InlineData("zh-Hans", "zh-CN")]
    public void ExtendedLocaleResolvesRegionalCulture(string culture, string expected)
    {
        LocaleCatalog.GetSupportedLanguage(culture).Should().Be(expected);
    }

    [Fact]
    public void ExtendedLocalesTranslateThePrimaryWizardJourney()
    {
        string[] requiredKeys =
        [
            "launcher.wizard.window_title", "launcher.wizard.brand_promise", "launcher.wizard.back",
            "launcher.wizard.step.intro", "launcher.wizard.step.host", "launcher.wizard.step.image",
            "launcher.wizard.step.usb", "launcher.wizard.step.create", "launcher.wizard.step.complete",
            "launcher.wizard.intro.title", "launcher.wizard.intro.description",
            "launcher.wizard.host.title", "launcher.wizard.host.description",
            "launcher.wizard.image.title", "launcher.wizard.image.description", "launcher.wizard.image.progress",
            "launcher.wizard.usb.title", "launcher.wizard.usb.description",
            "launcher.wizard.usb.confirm_erase",
            "launcher.wizard.create.title", "launcher.wizard.create.description", "launcher.wizard.create.keep_connected",
            "launcher.wizard.complete.title", "launcher.wizard.complete.description", "launcher.wizard.complete.finish",
            "launcher.wizard.vm.checking_usb", "launcher.wizard.vm.checking_usb_details",
            "launcher.wizard.vm.checking_runtime", "launcher.wizard.vm.checking_runtime_details",
            "launcher.wizard.vm.locking_storage", "launcher.wizard.vm.locking_storage_details",
            "launcher.wizard.vm.starting", "launcher.wizard.vm.starting_patience", "launcher.wizard.vm.starting_details",
            "launcher.wizard.vm.connecting", "launcher.wizard.vm.connecting_details", "launcher.wizard.vm.keep_usb",
            "launcher.wizard.vm.running", "launcher.wizard.vm.running_details", "launcher.wizard.vm.stop",
            "launcher.wizard.vm.stopping", "launcher.wizard.vm.stopped", "launcher.wizard.vm.safe_to_eject",
            "launcher.wizard.vm.usb_removed_title", "launcher.wizard.vm.usb_removed_message"
        ];

        foreach (var language in new[] { "de", "es", "fr", "pt-BR", "pl", "tr", "zh-CN" })
        {
            LocaleCatalog.GetLocalizationKeys(language).Should().Contain(requiredKeys);
        }
    }
}
