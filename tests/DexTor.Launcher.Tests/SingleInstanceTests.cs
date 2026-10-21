using FluentAssertions;

using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class SingleInstanceTests
{
    [Fact]
    public void SecondLauncherMutexIsDetected()
    {
        var name = $"Local\\DexTor.Tests.{Guid.NewGuid():N}";
        using var first = Program.TryAcquireSingleInstance(name, out var firstInstance);
        using var second = Program.TryAcquireSingleInstance(name, out var secondInstance);

        firstInstance.Should().BeTrue();
        secondInstance.Should().BeFalse();
    }
}
