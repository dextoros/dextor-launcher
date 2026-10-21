using FluentAssertions;

using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class DataVolumeReadinessTests
{
    [Fact]
    public async Task RetriesUntilWindowsReportsTheNewVolumeReady()
    {
        var probes = 0;
        var delays = 0;

        await HybridUsbProvisioner.WaitForDataVolumeAsync(
            @"Z:\",
            _ => ++probes == 3,
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            maxAttempts: 5);

        probes.Should().Be(3);
        delays.Should().Be(2);
    }

    [Fact]
    public async Task ReturnsActionableErrorAfterMountTimeout()
    {
        var action = () => HybridUsbProvisioner.WaitForDataVolumeAsync(
            @"Z:\",
            _ => false,
            (_, _) => Task.CompletedTask,
            maxAttempts: 2);

        await action.Should().ThrowAsync<IOException>()
            .WithMessage("*finish mounting*Keep the USB connected*");
    }
}
