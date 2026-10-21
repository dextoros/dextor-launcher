using System.Text;
using FluentAssertions;
using Xunit;

using DexTor.SessionHost;

namespace DexTor.Launcher.Tests;

public sealed class GuestDisplayTests
{
    [Theory]
    [InlineData("P6\n640 480\n255\n", 640, 480, true)]
    [InlineData("P6\n# made by QEMU\n720 400\n255\n", 720, 400, false)]
    [InlineData("P6 1024\t768 255\n", 1024, 768, false)]
    public void TheVmWindowStaysHiddenOnlyWhileQemuShowsItsPlaceholder(string header, int width, int height, bool placeholder)
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(header + "\0\0\0"));

        var size = GuestDisplay.ReadPpmSize(stream);

        size.Should().Be((width, height));
        GuestDisplay.IsPlaceholder(width, height).Should().Be(placeholder);
    }

    [Fact]
    public void AFileThatIsNotADumpIsNotTakenForOne()
    {
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes("\x89PNG\r\n"));
        GuestDisplay.ReadPpmSize(stream).Should().BeNull();
    }

    private static MemoryStream Dump(int width, int height, Action<byte[]>? paint = null)
    {
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        var pixels = new byte[width * height * 3];
        paint?.Invoke(pixels);
        var stream = new MemoryStream();
        stream.Write(header);
        stream.Write(pixels);
        stream.Position = 0;
        return stream;
    }

    [Fact]
    public void TheWindowWaitsThroughTheFirmwareGrubAndTheBlackBeforeTheSplash()
    {
        // SeaBIOS, "Booting from DVD/CD..." and "Welcome to GRUB!" are 720x400 text, with text on it.
        using (var firmware = Dump(720, 400, pixels => Array.Fill(pixels, (byte)200, 0, 30_000)))
        {
            GuestDisplay.Classify(firmware).Should().Be(GuestScreen.TextMode);
        }

        using (var placeholder = Dump(640, 480, pixels => Array.Fill(pixels, (byte)200, 0, 30_000)))
        {
            GuestDisplay.Classify(placeholder).Should().Be(GuestScreen.Placeholder);
        }

        // The kernel has the display in its own mode, nothing drawn yet.
        using (var blank = Dump(1280, 800))
        {
            GuestDisplay.Classify(blank).Should().Be(GuestScreen.Blank);
        }

        GuestDisplay.IsAlive(GuestScreen.TextMode).Should().BeTrue("the firmware-hang check still sees the guest alive");
        GuestDisplay.IsAlive(GuestScreen.Placeholder).Should().BeFalse();
    }

    [Fact]
    public void ALogoOnBlackIsAPictureToShow()
    {
        const int width = 1280, height = 800;
        using var splash = Dump(width, height, pixels =>
        {
            // A 160x160 logo in the middle: 2.5% of the screen.
            for (var y = 320; y < 480; y++)
            for (var x = 560; x < 720; x++)
            {
                var offset = (y * width + x) * 3;
                pixels[offset] = 140;
                pixels[offset + 1] = 90;
                pixels[offset + 2] = 240;
            }
        });

        GuestDisplay.Classify(splash).Should().Be(GuestScreen.Picture);
    }
}
