using FluentAssertions;

using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class PhysicalDriveReadinessTests
{
    [Fact]
    public async Task RetriesPhysicalDriveOpenWhileWindowsReportsNotReady()
    {
        var attempts = 0;
        var delays = 0;

        await using var stream = await PhysicalHybridDiskWriter.OpenPhysicalDriveAsync(
            () => ++attempts < 3
                ? throw new IOException("The device is not ready.")
                : new MemoryStream(),
            (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            maxAttempts: 5);

        attempts.Should().Be(3);
        delays.Should().Be(2);
    }

    [Fact]
    public async Task DoesNotRetryPermissionFailures()
    {
        var attempts = 0;
        var action = () => PhysicalHybridDiskWriter.OpenPhysicalDriveAsync<MemoryStream>(
            () =>
            {
                attempts++;
                throw new UnauthorizedAccessException("denied");
            },
            (_, _) => Task.CompletedTask,
            maxAttempts: 5);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        attempts.Should().Be(1);
    }
}
