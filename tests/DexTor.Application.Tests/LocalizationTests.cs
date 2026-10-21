using System.Globalization;
using FluentAssertions;
using Xunit;

using DexTor.Application;

namespace DexTor.Application.Tests;

public class LocalizationTests
{
    [Theory]
    [InlineData("en", "Developer signature and SHA-256 checksum")]
    [InlineData("ru", "Подпись разработчика и контрольная сумма SHA-256")]
    [InlineData("ja-JP", "Developer signature and SHA-256 checksum")]
    public void GlassOverviewTextIsLocalizedWithEnglishFallback(string language, string expected)
    {
        new Localizer(language).T("launcher.wizard.intro.detail_image").Should().Be(expected);
    }

    [Fact]
    public void LocalizerUsesCurrentCultureLanguageWhenSupported()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("ru-RU");
        try
        {
            var localizer = new Localizer();
            localizer.T("launcher.warning").Should().Be("Проверка среды завершена с предупреждениями.");
            localizer.TryGetLanguage(out var language).Should().BeTrue();
            language.Should().Be("ru");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void LocalizerFallsBackToEnglishForUnsupportedCulture()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
        try
        {
            var localizer = new Localizer();
            localizer.T("launcher.warning").Should().Be("Environment check completed with warnings.");
            localizer.TryGetLanguage(out var language).Should().BeFalse();
            language.Should().Be("en");
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void MissingKeyFallsBackToEnglish()
    {
        var localizer = new Localizer();
        var text = localizer.T("does.not.exist");
        text.Should().Be("does.not.exist");
    }

    [Fact]
    public void UnsupportedLocaleFallsBackToEnglish()
    {
        LocaleCatalog.GetSupportedLanguage("zz-ZZ").Should().Be(Localizer.DefaultLanguage);
    }

    [Fact]
    public void SupportedLocaleRuReturnsRussian()
    {
        var culture = new CultureInfo("ru-RU");
        LocaleCatalog.GetSupportedLanguage(culture.Name).Should().Be("ru");
    }
}
