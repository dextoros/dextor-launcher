using System.Text.Json;
using DexTor.Qemu;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class StorageFullPauseTests
{
    private static QmpEvent Event(string name, string data) => new(name, JsonDocument.Parse(data).RootElement.Clone());

    [Fact]
    public void QemuPausingTheVmForWantOfSpaceIsRecognizedAsSuch()
    {
        // What QEMU sends with werror=enospc when the stick is full.
        SessionHostService.IsNoSpacePause(Event("BLOCK_IO_ERROR",
                """{"device":"","node-name":"dextor-data","operation":"write","action":"stop","nospace":true,"reason":"No space left on device"}"""))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"operation":"write","action":"stop","nospace":false,"reason":"Input/output error"}""")]
    [InlineData("""{"operation":"write","action":"report","nospace":true,"reason":"No space left on device"}""")]
    [InlineData("""{"operation":"read","action":"report","reason":"Input/output error"}""")]
    public void OtherStorageErrorsAreNotTakenForAFullStick(string data)
    {
        SessionHostService.IsNoSpacePause(Event("BLOCK_IO_ERROR", data)).Should().BeFalse();
    }

    [Fact]
    public void OnlyTheStorageNotYetOnTheStickCountsAgainstTheFreeSpace()
    {
        const long GB = 1024L * 1024 * 1024;
        // 25 GB image, 3 GB already written, 30 GB free: room enough.
        StorageSpace.Shortfall(25 * GB, 3 * GB, 30 * GB).Should().Be(0);
        // The operator filled the stick: 10 GB free, 22 GB still to come.
        StorageSpace.Shortfall(25 * GB, 3 * GB, 10 * GB).Should().Be(12 * GB);
        // A fully written image (an older stick) needs nothing more.
        StorageSpace.Shortfall(25 * GB, 25 * GB, 0).Should().Be(0);
    }
}
