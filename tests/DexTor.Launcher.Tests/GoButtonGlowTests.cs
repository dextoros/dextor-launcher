using System.Drawing;

using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// The go button's glow as one rounded shape: the same on all four sides and round at the
/// corners, at every display scale - no strips meeting in the corners, no square halo
/// corners, no side brighter than the others.
/// </summary>
public sealed class GoButtonGlowTests
{
    public static TheoryData<float, bool, bool> Cases()
    {
        var cases = new TheoryData<float, bool, bool>();
        foreach (var scale in new[] { 1f, 1.25f, 1.5f, 2f })
        {
            foreach (var inside in new[] { false, true })
            {
                cases.Add(scale, inside, false);
                cases.Add(scale, inside, true);
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void IsTheSameOnEverySide(float scale, bool inside, bool hover)
    {
        using var glow = Render(scale, inside, hover);
        for (var y = 0; y < glow.Height; y++)
        {
            for (var x = 0; x < glow.Width; x++)
            {
                var pixel = glow.GetPixel(x, y).ToArgb();
                glow.GetPixel(glow.Width - 1 - x, y).ToArgb().Should().Be(pixel, "left and right mirror each other at ({0},{1})", x, y);
                glow.GetPixel(x, glow.Height - 1 - y).ToArgb().Should().Be(pixel, "top and bottom mirror each other at ({0},{1})", x, y);
            }
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public void HaloIsRoundAtTheCorners(float scale)
    {
        var width = (int)(200 * scale);
        var height = (int)(40 * scale);
        using var glow = Render(scale, inside: false, hover: true);
        var spread = (glow.Width - width) / 2;
        var reach = (int)(WizardDesign.GoGlowReach * scale);
        // The halo's own corner is empty, as a rounded halo leaves it.
        glow.GetPixel(0, 0).A.Should().Be(0);
        foreach (var step in new[] { reach / 4, reach / 2 })
        {
            // Out from the middle of the top edge, and out diagonally from the corner by as
            // much on each axis: a square halo would be as strong there; a round one fades.
            var side = glow.GetPixel(spread + width / 2, spread - step).A;
            var corner = glow.GetPixel(spread - step, spread - step).A;
            corner.Should().BeLessThan(side);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void InnerGlowHasNoCreaseAlongTheCornerDiagonal(float scale)
    {
        using var glow = Render(scale, inside: true, hover: true);
        // Along a line parallel to the top edge, just inside it, the light changes smoothly:
        // a crease along the corner diagonal showed as a sudden change in slope there.
        var row = (int)(10 * scale);
        var slopes = new List<int>();
        for (var x = (int)(4 * scale); x < (int)(30 * scale); x++)
        {
            slopes.Add(glow.GetPixel(x + 1, row).A - glow.GetPixel(x, row).A);
        }

        for (var i = 1; i < slopes.Count; i++)
        {
            Math.Abs(slopes[i] - slopes[i - 1]).Should().BeLessThanOrEqualTo(4);
        }
    }

    private static Bitmap Render(float scale, bool inside, bool hover) =>
        WizardDesign.RenderGoGlow((int)(200 * scale), (int)(40 * scale), scale, new WizardDesign.GoLook(hover ? 1f : 0f, false), inside);
}
