using FluentAssertions;
using Xunit;

using DexTor.SessionHost;

namespace DexTor.Launcher.Tests;

public sealed class QemuWindowBrandingTests
{
    private const string Title = "DexTor OS";

    [Theory]
    [InlineData("QEMU (DexTor OS Virtual Machine)")]
    [InlineData("")]
    [InlineData("QEMU (DexTor OS Virtual Machine) - Press Right-Ctrl-G to exit grab")]
    [InlineData("DexTor OS Virtual Machine - Press Right-Ctrl-G to exit grab")]
    [InlineData("QEMU (DexTor OS Virtual Machine) [Paused]")]
    [InlineData("SDL_app")]
    public void TheWindowIsTitledJustDexTorWithoutQemusGrabHint(string qemuTitle)
    {
        QemuWindowManager.BrandTitle(qemuTitle, Title).Should().Be(Title);
    }

    [Theory]
    [InlineData(640, 400, 656, 439, 656, 519)]
    [InlineData(320, 200, 336, 239, 656, 519)]
    [InlineData(720, 400, 736, 439, 736, 519)]
    public void AWindowSmallerThan640By480IsGrownByTheMissingPictureArea(
        int clientWidth, int clientHeight, int windowWidth, int windowHeight, int expectedWidth, int expectedHeight)
    {
        QemuWindowManager.MinimumWindowSize(clientWidth, clientHeight, windowWidth, windowHeight)
            .Should().Be((expectedWidth, expectedHeight));
    }

    [Theory]
    [InlineData(640, 480)]
    [InlineData(1280, 800)]
    [InlineData(0, 0)]
    public void ALargeEnoughOrNotYetShownWindowIsLeftAlone(int clientWidth, int clientHeight)
    {
        QemuWindowManager.MinimumWindowSize(clientWidth, clientHeight, clientWidth + 16, clientHeight + 39).Should().BeNull();
    }

    private static readonly QemuWindowWatcher.Bounds Full = new(-8, -8, 1928, 1160);
    private static readonly QemuWindowWatcher.Bounds GuestTextMode = new(-8, -8, 728, 431);

    [Fact]
    public void BeforeTheGuestDrawsAWindowThatShowsAnywayIsHidden()
    {
        QemuWindowWatcher.Decide(QemuWindowWatcher.Mode.Hidden, visible: true, zoomed: false, iconic: false, GuestTextMode, null)
            .Should().Be(QemuWindowWatcher.WindowAction.Hide);
        QemuWindowWatcher.Decide(QemuWindowWatcher.Mode.Hidden, visible: false, zoomed: false, iconic: false, GuestTextMode, null)
            .Should().Be(QemuWindowWatcher.WindowAction.None);
    }

    [Fact]
    public void WhileBootingARestoredWindowIsMaximizedAgain()
    {
        QemuWindowWatcher.Decide(QemuWindowWatcher.Mode.Maximized, visible: true, zoomed: false, iconic: false, GuestTextMode, Full)
            .Should().Be(QemuWindowWatcher.WindowAction.Maximize);
    }

    [Fact]
    public void AMaximizedWindowSdlShrankToTheGuestModeGetsTheWholeScreenBack()
    {
        // Measured on QEMU 11.1: SDL resizes a maximized window to the guest's video mode and
        // the window still reports itself maximized.
        QemuWindowWatcher.Decide(QemuWindowWatcher.Mode.Maximized, visible: true, zoomed: true, iconic: false, GuestTextMode, Full)
            .Should().Be(QemuWindowWatcher.WindowAction.RestoreMaximizedBounds);
        QemuWindowWatcher.Decide(QemuWindowWatcher.Mode.Maximized, visible: true, zoomed: true, iconic: false, Full, Full)
            .Should().Be(QemuWindowWatcher.WindowAction.None);
    }

    [Fact]
    public void AMinimizedWindowOrOneAfterTheBootIsLeftToTheOperator()
    {
        QemuWindowWatcher.Decide(QemuWindowWatcher.Mode.Maximized, visible: true, zoomed: false, iconic: true, GuestTextMode, Full)
            .Should().Be(QemuWindowWatcher.WindowAction.None);
        QemuWindowWatcher.Decide(QemuWindowWatcher.Mode.Off, visible: true, zoomed: false, iconic: false, GuestTextMode, Full)
            .Should().Be(QemuWindowWatcher.WindowAction.None);
    }
}
