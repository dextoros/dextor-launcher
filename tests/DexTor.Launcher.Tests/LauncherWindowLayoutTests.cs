using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using DexTor.Application;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class LauncherWindowLayoutTests
{
    [Fact]
    public void PrepareActionRunsInsideMainWindowWithoutMovingContent()
    {
        var model = new LauncherPreflightPresentation(
            "DexTor Host Launcher",
            "Environment check completed with warnings.",
            "Review the checks below.",
            [
                new LauncherRequirementRow(
                    "WARN",
                    PreflightRequirementState.Warning,
                    "WHPX",
                    "WHPX is disabled.",
                    null,
                    "Enable virtualization and restart Windows.",
                    "Open instructions",
                    "https://example.test/instructions")
            ],
            LauncherPrimaryAction.Prepare,
            "Prepare this PC",
            null,
            null,
            true,
            true,
            false);

        LauncherWindow? window = null;
        var buttonsWereDisabledDuringPrepare = false;
        var enabledButtonNamesDuringPrepare = string.Empty;
        window = new LauncherWindow(
            model,
            new TestLocalizer(),
            () =>
            {
                buttonsWereDisabledDuringPrepare = FindControls<Button>(window!).All(button => !button.Enabled);
                enabledButtonNamesDuringPrepare = string.Join(", ", FindControls<Button>(window!).Where(button => button.Enabled).Select(button => button.Name));
                return Task.FromResult(new LauncherInlineActionResult(
                    true,
                    "Virtualization settings are now ready.",
                    "Restart Windows to finish."));
            });

        using (window)
        {
            window.CreateControl();
            PerformLayoutRecursively(window);
            var requirements = FindControls<FlowLayoutPanel>(window).First(panel => panel.AutoScroll);
            var requirementsBounds = requirements.Bounds;
            var preparePanel = window.Controls.Find("InlinePreparePanel", true).Should().ContainSingle().Subject;
            preparePanel.Parent.Should().BeSameAs(requirements.Parent);
            preparePanel.Bounds.IntersectsWith(requirements.Bounds).Should().BeTrue();
            var primary = window.Controls.Find("PrimaryActionButton", true).Should().ContainSingle().Subject.Should().BeOfType<Button>().Subject;

            InvokeButtonClick(primary);
            PerformLayoutRecursively(window);

            buttonsWereDisabledDuringPrepare.Should().BeTrue($"enabled buttons: {enabledButtonNamesDuringPrepare}");
            window.DialogResult.Should().Be(DialogResult.None);
            preparePanel.Height.Should().BeGreaterThan(0);
            requirements.Bounds.Should().Be(requirementsBounds);
            requirements.Padding.Bottom.Should().BeGreaterThan(0);
            window.Controls.Find("InlinePrepareSpinner", true).Single().Visible.Should().BeFalse();
            window.Controls.Find("InlinePrepareTitle", true).Single().Text.Should().Be("Virtualization settings are now ready.");
            window.Controls.Find("InlinePrepareDetail", true).Single().Text.Should().Be("Restart Windows to finish.");
            primary.Enabled.Should().BeFalse();
            window.Controls.Find("RefreshButton", true).Single().Enabled.Should().BeTrue();
            window.Controls.Find("CloseButton", true).Single().Enabled.Should().BeTrue();
        }
    }

    [Fact]
    public void RequirementCardPaintsOpaqueBackgroundInsteadOfRetainingPreviousFrame()
    {
        using var host = new Panel
        {
            BackColor = Color.Red,
            Size = new Size(360, 160)
        };
        using var card = new GlassPanel
        {
            Bounds = new Rectangle(10, 10, 320, 120),
            RevealProgress = 1f
        };
        host.Controls.Add(card);
        host.CreateControl();

        using var firstFrame = new Bitmap(card.Width, card.Height);
        card.DrawToBitmap(firstFrame, card.ClientRectangle);
        var firstCenter = firstFrame.GetPixel(card.Width / 2, card.Height / 2);

        host.BackColor = Color.Lime;
        using var secondFrame = new Bitmap(card.Width, card.Height);
        card.DrawToBitmap(secondFrame, card.ClientRectangle);
        var secondCenter = secondFrame.GetPixel(card.Width / 2, card.Height / 2);

        secondCenter.Should().Be(firstCenter);
        secondCenter.Should().NotBe(Color.Red);
        secondCenter.Should().NotBe(Color.Lime);
    }

    [Fact]
    public void MainWindowIncludesConsistentDexTorOsFooter()
    {
        var model = new LauncherPreflightPresentation(
            "DexTor Host Launcher",
            "Environment is ready.",
            "Continue.",
            [],
            LauncherPrimaryAction.Continue,
            "Continue",
            null,
            null,
            true,
            false,
            false);

        using var window = new LauncherWindow(model, new TestLocalizer());
        window.CreateControl();
        PerformLayoutRecursively(window);

        var footer = window.Controls.Find("DexTorBrandFooter", true).Should().ContainSingle().Subject;
        var root = window.Controls.Find("LauncherRoot", true).Should().ContainSingle().Subject;
        root.Bottom.Should().BeLessThanOrEqualTo(footer.Top);
        // The footer carries the running version, so an old copy can always be told from a new one.
        footer.Controls.Cast<Control>().Should().Contain(control => control.Text == "launcher.brand.footer  ·  v" + LauncherBranding.DisplayVersion);
        LauncherBranding.DisplayVersion.Should().NotBeNullOrWhiteSpace().And.NotContain("+");
        window.Controls.Find("DexTorBrandSiteLink", true).Single().Tag.Should().Be("https://dextoros.com/");
    }

    [Fact]
    public void RequirementCardsStackHeaderAndWrappedTextWithoutOverlap()
    {
        var rows = new[]
        {
            new LauncherRequirementRow(
                "OK",
                PreflightRequirementState.Ok,
                "Live image source",
                "Latest live ISO source is available and this deliberately long description verifies that the text can wrap onto another line.",
                "Resolved ISO: https://dextoros.com/public/1.1.29/DextorOS_1_1_29.iso",
                "No action is required.",
                null,
                null),
            new LauncherRequirementRow(
                "WARN",
                PreflightRequirementState.Warning,
                "Windows hypervisor platform",
                "WHPX is disabled. You can enable it via Prepare this PC.",
                null,
                "Enable the Windows feature and restart the computer before continuing.",
                "Open instructions",
                "https://example.test/instructions")
        };
        var model = new LauncherPreflightPresentation(
            "DexTor Host Launcher",
            "Environment check completed with warnings.",
            "Review the checks below.",
            rows,
            LauncherPrimaryAction.Prepare,
            "Prepare this PC",
            null,
            null,
            true,
            true,
            false);

        using var window = new LauncherWindow(model, new TestLocalizer());
        window.CreateControl();
        PerformLayoutRecursively(window);

        var cards = FindControls<GlassPanel>(window).ToArray();
        cards.Should().HaveCount(rows.Length);

        foreach (var card in cards)
        {
            var header = card.Controls.Find("RequirementHeader", true).Should().ContainSingle().Subject;
            var body = card.Controls.Find("RequirementBody", true).Should().ContainSingle().Subject;

            header.Height.Should().BeGreaterThan(0);
            body.Height.Should().BeGreaterThan(0);
            body.Top.Should().BeGreaterThanOrEqualTo(header.Bottom);

            var textBlocks = body.Controls.Cast<Control>().OrderBy(control => control.Top).ToArray();
            for (var i = 1; i < textBlocks.Length; i++)
            {
                textBlocks[i].Top.Should().BeGreaterThanOrEqualTo(textBlocks[i - 1].Bottom);
            }

            card.Height.Should().BeGreaterThanOrEqualTo(body.Bottom + card.Padding.Bottom);
        }
    }

    private static IEnumerable<T> FindControls<T>(Control root)
        where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindControls<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void PerformLayoutRecursively(Control root)
    {
        foreach (Control child in root.Controls)
        {
            PerformLayoutRecursively(child);
        }

        root.PerformLayout();
    }

    private static void InvokeButtonClick(Button button)
    {
        var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        onClick.Should().NotBeNull();
        onClick!.Invoke(button, [EventArgs.Empty]);
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
