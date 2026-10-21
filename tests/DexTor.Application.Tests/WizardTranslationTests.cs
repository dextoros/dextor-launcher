using FluentAssertions;
using Xunit;

using DexTor.Application;

namespace DexTor.Application.Tests;

public sealed partial class WizardTranslationTests
{
    /// <summary>Strings that read the same in every language: names, units, symbols.</summary>
    private static readonly HashSet<string> SameEverywhere = new(StringComparer.Ordinal)
    {
        "SHA-256", "DexTor OS", "USB", "Windows", "BIOS/UEFI", "—", "OK", "Beta"
    };

    [Theory]
    [InlineData("ru")]
    [InlineData("uk")]
    public void TheWizardIsTranslatedNotLeftInEnglish(string language)
    {
        // A tester running the wizard in Russian reported "CHECK RESULTS" in English.
        var english = new Localizer("en");
        var translated = new Localizer(language);
        var untranslated = LocaleCatalog.GetLocalizationKeys("en")
            .Where(key => key.StartsWith("launcher.wizard.", StringComparison.Ordinal))
            .Where(key =>
            {
                var text = english.T(key);
                return PlaceholderPattern().Replace(text, string.Empty).Any(char.IsLetter)
                    && !SameEverywhere.Contains(text.Trim())
                    && string.Equals(text, translated.T(key), StringComparison.Ordinal);
            })
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        string.Join(", ", untranslated).Should().BeEmpty($"every wizard string must be in '{language}'");
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\{[a-z_]+\}")]
    private static partial System.Text.RegularExpressions.Regex PlaceholderPattern();
}
