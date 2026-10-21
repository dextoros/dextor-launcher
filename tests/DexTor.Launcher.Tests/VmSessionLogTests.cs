using FluentAssertions;
using Xunit;

using DexTor.SessionHost;

namespace DexTor.Launcher.Tests;

public sealed class VmSessionLogTests
{
    [Fact]
    public void EveryStartLeavesALogOfHowQemuWasStartedAndHowItEnded()
    {
        var session = Guid.NewGuid();

        VmSessionLog.Started(session, @"C:\qemu\qemu-system-x86_64.exe", ["-accel", "whpx", "-cpu", "host", "-name", "DexTor OS Virtual Machine"], "host", 8192, 8);
        VmSessionLog.Note(session, "QEMU closed unexpectedly after 12 s: whpx: injection failed");

        var log = File.ReadAllText(VmSessionLog.PathFor(session));
        log.Should().Contain("profile host memory 8192 MiB processors 8");
        log.Should().Contain("args -accel whpx -cpu host -name \"DexTor OS Virtual Machine\"");
        log.Should().Contain("injection failed");
        VmSessionLog.Directory.Should().StartWith(DexTor.Contracts.DexTorStorage.Root, "tests never write to the real %LOCALAPPDATA%");
    }
}
