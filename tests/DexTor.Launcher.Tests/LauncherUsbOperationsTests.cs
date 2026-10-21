using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class LauncherUsbOperationsTests
{
    [Fact]
    public void TryOpenDexTorHelpReturnsNoUsbForMissingPath()
    {
        var (success, errorKey, solutionKey) = LauncherUsbOperations.TryOpenDexTorHelp(null);

        success.Should().BeFalse();
        errorKey.Should().Be("launcher.action.open.error.no_usb");
        solutionKey.Should().Be("launcher.action.open.solution.no_usb");
    }

    [Fact]
    public void TryOpenDexTorHelpReturnsNotFoundForMissingRoot()
    {
        var (success, errorKey, solutionKey) = LauncherUsbOperations.TryOpenDexTorHelp(@"Z:\definitely-missing-path\that-does-not-exist");

        success.Should().BeFalse();
        errorKey.Should().Be("launcher.action.open.error.not_found");
        solutionKey.Should().Be("launcher.action.open.solution");
    }

    [Fact]
    public void TryOpenDexTorHelpReturnsNotFoundForInvalidPath()
    {
        var (success, errorKey, solutionKey) = LauncherUsbOperations.TryOpenDexTorHelp(@"C:\bad|path");

        success.Should().BeFalse();
        errorKey.Should().Be("launcher.action.open.error.not_found");
        solutionKey.Should().Be("launcher.action.open.solution");
    }

    [Fact]
    public void TryStartEjectFlowReturnsNoUsbForMissingPath()
    {
        var (success, errorKey, solutionKey) = LauncherUsbOperations.TryStartEjectFlow(string.Empty);

        success.Should().BeFalse();
        errorKey.Should().Be("launcher.action.eject.error.no_usb");
        solutionKey.Should().Be("launcher.action.eject.solution.no_usb");
    }

    [Fact]
    public void TryStartEjectFlowReturnsNotFoundForMissingRoot()
    {
        var (success, errorKey, solutionKey) = LauncherUsbOperations.TryStartEjectFlow(@"Z:\definitely-missing-path\that-does-not-exist");

        success.Should().BeFalse();
        errorKey.Should().Be("launcher.action.eject.error.not_found");
        solutionKey.Should().Be("launcher.action.eject.solution.no_usb");
    }

    [Fact]
    public void TryStartEjectFlowReturnsNotFoundForInvalidPath()
    {
        var (success, errorKey, solutionKey) = LauncherUsbOperations.TryStartEjectFlow(@"C:\bad|path");

        success.Should().BeFalse();
        errorKey.Should().Be("launcher.action.eject.error.not_found");
        solutionKey.Should().Be("launcher.action.eject.solution.no_usb");
    }
}
