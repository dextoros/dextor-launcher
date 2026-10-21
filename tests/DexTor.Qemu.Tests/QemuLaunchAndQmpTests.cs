using System.Text.Json;
using DexTor.Contracts;
using FluentAssertions;
using Xunit;

namespace DexTor.Qemu.Tests;

public sealed class QemuLaunchAndQmpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-qemu-plan-" + Guid.NewGuid().ToString("N"));

    public QemuLaunchAndQmpTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void BuildUsesWhpxReadOnlyCdPersistentVirtioAndQmpStdio()
    {
        var executable = Write("qemu-system-x86_64.exe");
        var iso = Write("DexTor OS.iso");
        var data = Write("data.img");
        var request = new SessionStartRequest(Guid.NewGuid(), iso, iso, data, new string('a', 64), 4096, 4);

        var plan = QemuLaunchPlanBuilder.Build(executable, new ResolvedSystemImage(iso, SystemImageLocation.TemporaryCache), request);

        plan.Arguments.Should().ContainInOrder("-accel", "whpx");
        plan.Arguments.Should().NotContain("-cpu");
        plan.Arguments.Should().ContainInOrder("-smp", "cores=4", "-m", "4096M");
        plan.Arguments.Should().ContainInOrder("-boot", "once=d,menu=off,splash-time=0");
        plan.Arguments.Should().ContainInOrder("-netdev", "user,id=dextor-net", "-device", "virtio-net-pci,netdev=dextor-net,romfile=");
        plan.Arguments.Should().NotContain("-fw_cfg", "a request without guest settings leaves the guest as it boots from the stick");
        plan.Arguments.Should().Contain(argument => argument.Contains("if=ide,index=2,media=cdrom,readonly=on,format=raw", StringComparison.Ordinal));
        plan.Arguments.Should().ContainInOrder("-object", "iothread,id=dataio");
        plan.Arguments.Should().Contain(argument => argument.Contains("if=none,id=dextor-data,format=raw,cache=writeback,aio=threads", StringComparison.Ordinal));
        plan.Arguments.Should().ContainInOrder("-device", "virtio-blk-pci,drive=dextor-data,serial=DEXTOR_DATA,iothread=dataio");
        QemuLaunchPlanBuilder.GuestPrivateStorageByIdPath.Should().Be("/dev/disk/by-id/virtio-DEXTOR_DATA");
        plan.Arguments.Should().ContainInOrder("-device", "ich9-usb-ehci1", "-device", "usb-tablet");
        plan.Arguments.Should().ContainInOrder("-vga", "virtio");
        plan.Arguments.Should().ContainInOrder("-display", "sdl,gl=off,grab-mod=rctrl");
        plan.Arguments.Should().ContainInOrder("-name", "DexTor OS");
        // GRUB in DexTor OS hides its menu when it finds this OEM string.
        plan.Arguments.Should().ContainInOrder("-smbios", "type=11,value=DEXTOR_LAUNCHER");
        plan.Arguments.Should().ContainInOrder(
            "-audiodev", "sdl,id=dextor-audio",
            "-device", "intel-hda",
            "-device", "hda-output,audiodev=dextor-audio");
        plan.Arguments.Should().NotContain(argument => argument.Contains("hda-duplex", StringComparison.Ordinal)
            || argument.Contains("hda-micro", StringComparison.Ordinal), "the host microphone is never offered to the guest");
        plan.Arguments.Should().NotContain("-full-screen");
        plan.Arguments.Should().ContainInOrder("-qmp", "stdio");
        plan.Arguments.Should().NotContain("-snapshot");
    }

    [Fact]
    public void TheWizardsLanguageAndTheBuildChannelReachTheGuestThroughFwCfg()
    {
        var executable = Write("qemu-system-x86_64.exe");
        var iso = Write("DexTor OS.iso");
        var data = Write("data.img");
        var request = new SessionStartRequest(Guid.NewGuid(), iso, iso, data, new string('a', 64), 4096, 4,
            GuestLocale: "ru_RU.UTF-8", BuildChannel: "test");

        var plan = QemuLaunchPlanBuilder.Build(executable, new ResolvedSystemImage(iso, SystemImageLocation.TemporaryCache), request);

        plan.Arguments.Should().ContainInOrder("-fw_cfg", "name=opt/org.dextoros/locale,string=ru_RU.UTF-8");
        plan.Arguments.Should().ContainInOrder("-fw_cfg", "name=opt/org.dextoros/channel,string=test");
    }

    [Fact]
    public void AGuestSettingThatIsNotAPlainTokenIsLeftOutRatherThanSplitByQemu()
    {
        var executable = Write("qemu-system-x86_64.exe");
        var iso = Write("DexTor OS.iso");
        var data = Write("data.img");
        var request = new SessionStartRequest(Guid.NewGuid(), iso, iso, data, new string('a', 64), 4096, 4,
            GuestLocale: "ru_RU.UTF-8,file=C:/x", BuildChannel: "test");

        var plan = QemuLaunchPlanBuilder.Build(executable, new ResolvedSystemImage(iso, SystemImageLocation.TemporaryCache), request);

        plan.Arguments.Should().NotContain(argument => argument.Contains("locale", StringComparison.Ordinal));
        plan.Arguments.Should().ContainInOrder("-fw_cfg", "name=opt/org.dextoros/channel,string=test");
    }

    [Fact]
    public async Task HandshakeAcceptsGreetingAndNegotiatesCapabilitiesAndVersion()
    {
        var input = string.Join(Environment.NewLine,
            "{\"QMP\":{\"version\":{},\"capabilities\":[]}}",
            "{\"return\":{}}",
            "{\"return\":{\"qemu\":{\"major\":11,\"minor\":1,\"micro\":0}}}",
            "{\"return\":{\"status\":\"running\",\"running\":true}}");
        using var reader = new StringReader(input);
        using var writer = new StringWriter();
        await using var channel = new QmpStreamChannel(reader, writer);

        await channel.HandshakeAsync();

        var commands = writer.ToString();
        commands.Should().Contain("qmp_capabilities");
        commands.Should().Contain("query-version");
        commands.Should().Contain("query-status");
    }

    [Fact]
    public async Task HandshakeContinuesPausedGuestAndRequiresRunningState()
    {
        var input = string.Join(Environment.NewLine,
            "{\"QMP\":{}}",
            "{\"return\":{}}",
            "{\"return\":{}}",
            "{\"return\":{\"status\":\"paused\"}}",
            "{\"return\":{}}",
            "{\"return\":{\"status\":\"running\"}}");
        using var writer = new StringWriter();
        await using var channel = new QmpStreamChannel(new StringReader(input), writer);

        await channel.HandshakeAsync();

        writer.ToString().Should().Contain("\"execute\":\"cont\"");
    }

    [Fact]
    public async Task HandshakeRejectsQmpError()
    {
        var input = string.Join(Environment.NewLine,
            "{\"QMP\":{}}",
            "{\"error\":{\"class\":\"CommandNotFound\"}}");
        await using var channel = new QmpStreamChannel(new StringReader(input), new StringWriter());

        var action = () => channel.HandshakeAsync();

        await action.Should().ThrowAsync<IOException>();
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }
}
