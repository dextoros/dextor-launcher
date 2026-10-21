using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Xunit;

using DexTor.Platform.Windows;

namespace DexTor.Platform.Windows.Tests;

public class WindowsHostProbeTests
{
    [Fact]
    public void EnableWhpxEnablesWindowsHypervisorPlatformAndBootLaunch()
    {
        var commands = new List<ProcessStartInfo>();

        var result = WindowsHostProbe.EnableWhpx(info =>
        {
            commands.Add(info);
            return 0;
        });

        result.Should().BeTrue();
        commands.Should().HaveCount(2);
        commands[0].FileName.Should().Be("dism.exe");
        commands[0].Arguments.Should().Contain("/FeatureName:HypervisorPlatform");
        commands[0].Arguments.Should().NotContain("Microsoft-Hyper-V");
        commands[1].FileName.Should().Be("bcdedit.exe");
        commands[1].Arguments.Should().Be("/set hypervisorlaunchtype auto");
    }

    [Fact]
    public void EnableWhpxStopsWhenWindowsFeatureCannotBeEnabled()
    {
        var commandCount = 0;

        var result = WindowsHostProbe.EnableWhpx(_ =>
        {
            commandCount++;
            return 1;
        });

        result.Should().BeFalse();
        commandCount.Should().Be(1);
    }

    [Theory]
    [InlineData(0, 1UL, 8U, true)]
    [InlineData(0, 0UL, 8U, false)]
    [InlineData(-1, 1UL, 8U, false)]
    [InlineData(0, 1UL, 0U, false)]
    public void WhpxDetectionUsesHypervisorCapability(
        int hresult,
        ulong value,
        uint writtenSize,
        bool expected)
    {
        WindowsHostProbe.IsHypervisorCapabilityPresent(hresult, value, writtenSize)
            .Should().Be(expected);
    }

    [Fact]
    public async Task EnumerateUsbAsyncRespectsCancellation()
    {
        var probe = new WindowsHostProbe();
        using var token = new CancellationTokenSource();
        token.Cancel();

        var act = () => probe.EnumerateUsbAsync(token.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task IsUsbSafeForProvisioningAsyncEmptyInstanceIdIsFalse()
    {
        var probe = new WindowsHostProbe();
        var result = await probe.IsUsbSafeForProvisioningAsync(string.Empty, CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsUsbSafeForProvisioningAsyncNonExistingInstanceIsFalse()
    {
        var probe = new WindowsHostProbe();
        var result = await probe.IsUsbSafeForProvisioningAsync("Z:\\__definitely__missing__", CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsWhpxEnabledAsyncDoesNotThrow()
    {
        var probe = new WindowsHostProbe();
        var result = await probe.IsWhpxEnabledAsync(CancellationToken.None);
        (result == true || result == false).Should().BeTrue();
    }
}
