using System.Diagnostics;
using System.Reflection;
using DexTor.Application;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Turning on Windows Hypervisor Platform from the Store launcher, which cannot be started
/// as administrator.
/// </summary>
public sealed class ElevatedWhpxEnablerTests
{
    [Fact]
    public void ElevatedCopyIsTheLauncherItselfThroughTheUacPrompt()
    {
        var startInfo = ElevatedWhpxEnabler.BuildStartInfo(@"C:\Program Files\WindowsApps\DexTor\DexTor.Launcher.exe");

        startInfo.FileName.Should().Be(@"C:\Program Files\WindowsApps\DexTor\DexTor.Launcher.exe");
        startInfo.Arguments.Should().Be(ElevatedWhpxEnabler.ModeArgument);
        startInfo.Verb.Should().Be("runas");
        startInfo.UseShellExecute.Should().BeTrue("only ShellExecute shows the UAC prompt");
        startInfo.WindowStyle.Should().Be(ProcessWindowStyle.Hidden);
    }

    [Fact]
    public async Task PackagedLauncherWithoutAdminRightsElevatesForTheStep()
    {
        var calls = await RunPrepareFlow(isAdmin: false, isPackaged: true);

        calls.Elevated.Should().Be(1);
        calls.Direct.Should().Be(0);
        calls.Result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task PortableLauncherWithoutAdminRightsStillAsksToBeStartedAsAdministrator()
    {
        var calls = await RunPrepareFlow(isAdmin: false, isPackaged: false);

        calls.Elevated.Should().Be(0);
        calls.Direct.Should().Be(0);
        calls.Result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task ElevatedLauncherEnablesTheFeatureItself()
    {
        var calls = await RunPrepareFlow(isAdmin: true, isPackaged: true);

        calls.Elevated.Should().Be(0);
        calls.Direct.Should().Be(1);
    }

    private static async Task<(int Elevated, int Direct, LauncherInlineActionResult Result)> RunPrepareFlow(bool isAdmin, bool isPackaged)
    {
        var type = typeof(Program);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        var adminField = type.GetField("IsCurrentUserAdminCallback", flags)!;
        var packagedField = type.GetField("IsPackagedCallback", flags)!;
        var directField = type.GetField("TryEnableVirtualizationCallback", flags)!;
        var elevatedField = type.GetField("TryEnableVirtualizationElevatedCallback", flags)!;
        var method = type.GetMethod("RunPrepareFlowInlineAsync", flags)!;
        var original = (adminField.GetValue(null), packagedField.GetValue(null), directField.GetValue(null), elevatedField.GetValue(null));
        var elevated = 0;
        var direct = 0;
        try
        {
            adminField.SetValue(null, new Func<bool>(() => isAdmin));
            packagedField.SetValue(null, new Func<bool>(() => isPackaged));
            directField.SetValue(null, new Func<bool>(() => { direct++; return true; }));
            elevatedField.SetValue(null, new Func<bool>(() => { elevated++; return true; }));

            var result = await (Task<LauncherInlineActionResult>)method.Invoke(null, [new Localizer()])!;
            return (elevated, direct, result);
        }
        finally
        {
            adminField.SetValue(null, original.Item1);
            packagedField.SetValue(null, original.Item2);
            directField.SetValue(null, original.Item3);
            elevatedField.SetValue(null, original.Item4);
        }
    }
}
