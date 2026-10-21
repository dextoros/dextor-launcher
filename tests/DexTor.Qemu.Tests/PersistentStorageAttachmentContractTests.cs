using DexTor.Contracts;
using FluentAssertions;
using Xunit;

namespace DexTor.Qemu.Tests;

/// <summary>
/// Regression guard for the cross-project storage transport contract. DexTor OS resolves
/// persistent storage through <c>/dev/disk/by-id/virtio-DEXTOR_DATA</c>, so the attachment
/// below is an interface shared with the Linux side, not a launcher implementation detail.
/// Changing it silently breaks Secure Storage discovery in the guest.
/// </summary>
public sealed class PersistentStorageAttachmentContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-storage-contract-" + Guid.NewGuid().ToString("N"));

    public PersistentStorageAttachmentContractTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PersistentStorageIsAttachedAsAWritableRawVirtioDiskCarryingTheAgreedSerial()
    {
        var plan = BuildPlan(out var dataImagePath);

        var driveIndex = plan.Arguments.ToList().FindIndex(argument =>
            argument.Contains($"id={QemuLaunchPlanBuilder.PrivateStorageDriveId}", StringComparison.Ordinal));
        driveIndex.Should().BeGreaterThan(0);
        plan.Arguments[driveIndex - 1].Should().Be("-drive");

        var drive = plan.Arguments[driveIndex];
        drive.Should().Contain($"file={dataImagePath.Replace('\\', '/')}");
        drive.Should().Contain("if=none");
        drive.Should().Contain($"id={QemuLaunchPlanBuilder.PrivateStorageDriveId}");
        drive.Should().Contain("format=raw");
        drive.Should().Contain("cache=writeback", "guest flushes must reach the stick so LUKS/ext4 survive an unplug");
        drive.Should().NotContain("cache=unsafe", "unsafe ignores guest flushes and risks LUKS/ext4 corruption on unplug");
        drive.Should().Contain("aio=threads", "slow sticks must not block submission in QEMU's main loop");
        drive.Should().Contain("werror=enospc", "a full stick must pause the VM, not hand the guest an I/O error");
        drive.Should().NotContain("readonly", "the guest owns this image and must be able to write it");
        drive.Should().NotContain("snapshot", "writes must land in the real file, not a disposable overlay");

        plan.Arguments.Should().ContainInOrder(
            "-device",
            $"virtio-blk-pci,drive={QemuLaunchPlanBuilder.PrivateStorageDriveId},serial={QemuLaunchPlanBuilder.PrivateStorageSerial},iothread={QemuLaunchPlanBuilder.PrivateStorageIoThreadId}");
    }

    [Fact]
    public void PersistentStorageIsServedByItsOwnIoThreadSoASlowStickCannotFreezeTheVcpus()
    {
        // Without an iothread, virtio-blk requests are submitted and completed in QEMU's main
        // loop under the global lock the vCPUs also need: a slow USB stick holding the storage
        // file froze the whole VM for 30–50 s.
        var plan = BuildPlan(out _);
        var arguments = plan.Arguments.ToList();

        var objectIndex = arguments.FindIndex(argument =>
            argument == $"iothread,id={QemuLaunchPlanBuilder.PrivateStorageIoThreadId}");
        objectIndex.Should().BeGreaterThan(0);
        arguments[objectIndex - 1].Should().Be("-object");

        var deviceIndex = arguments.FindIndex(argument =>
            argument.StartsWith("virtio-blk-pci,", StringComparison.Ordinal)
            && argument.Contains($"drive={QemuLaunchPlanBuilder.PrivateStorageDriveId}", StringComparison.Ordinal));
        deviceIndex.Should().BeGreaterThan(0);
        arguments[deviceIndex - 1].Should().Be("-device");
        arguments[deviceIndex].Should().Contain($"serial={QemuLaunchPlanBuilder.PrivateStorageSerial}");
        arguments[deviceIndex].Should().Contain($"iothread={QemuLaunchPlanBuilder.PrivateStorageIoThreadId}");
        objectIndex.Should().BeLessThan(deviceIndex, "QEMU resolves the iothread when the device is created");
    }

    [Fact]
    public void TheGuestVisibleStorageIdentityIsUnchanged()
    {
        QemuLaunchPlanBuilder.PrivateStorageSerial.Should().Be("DEXTOR_DATA");
        QemuLaunchPlanBuilder.PrivateStorageDriveId.Should().Be("dextor-data");
        QemuLaunchPlanBuilder.PrivateStorageIoThreadId.Should().Be("dataio");
        QemuLaunchPlanBuilder.GuestPrivateStorageByIdPath.Should().Be("/dev/disk/by-id/virtio-DEXTOR_DATA");
    }

    [Fact]
    public void SessionOwnershipIsNeverCommunicatedToTheGuest()
    {
        var plan = BuildPlan(out var dataImagePath);
        var identity = DexTorSessionIdentity.ForDataImage(dataImagePath);

        // The lock is host-side only. Nothing about it may reach QEMU's command line.
        var commandLine = string.Join(' ', plan.Arguments);
        commandLine.Should().NotContain(identity.Fingerprint);
        commandLine.Should().NotContain("DexTor.Session");
        commandLine.Should().NotContain("Global\\");
        commandLine.Should().NotContain("Local\\");
    }

    [Fact]
    public void NoCredentialCarryingChannelIsExposedToTheGuest()
    {
        var plan = BuildPlan(out _);

        // Secure Storage passwords belong to the Linux side alone. These are the argument
        // families that could smuggle one into the guest.
        plan.Arguments.Should().NotContain("-fw_cfg");
        // The one SMBIOS string is a fixed marker for the guest's GRUB, never anything variable.
        var arguments = plan.Arguments.ToList();
        for (var index = arguments.IndexOf("-smbios"); index >= 0; index = arguments.IndexOf("-smbios", index + 1))
        {
            arguments[index + 1].Should().Be($"type=11,value={QemuLaunchPlanBuilder.LauncherOemString}");
        }

        plan.Arguments.Should().NotContain("-append");
        plan.Arguments.Should().NotContain("-kernel");
        plan.Arguments.Should().NotContain("-initrd");

        var commandLine = string.Join(' ', plan.Arguments);
        foreach (var term in new[] { "password", "passphrase", "luks", "secret", "cryptsetup" })
        {
            commandLine.Should().NotContainEquivalentOf(term);
        }
    }

    [Fact]
    public void TheGuestStartsInTheSizeOfTheMaximizedWindowAndLearnsTheWindowsScale()
    {
        var executable = Write("qemu-system-x86_64.exe");
        var iso = Write("system.img");
        var data = Write("data.img");
        var request = new SessionStartRequest(Guid.NewGuid(), iso, iso, data, new string('a', 64), 4096, 4,
            DisplayWidth: 2560, DisplayHeight: 1377, DisplayScalePercent: 150);

        var arguments = QemuLaunchPlanBuilder.Build(executable, new ResolvedSystemImage(iso, SystemImageLocation.UsbFallback), request).Arguments;

        // Its first mode is the screen's, so nothing is stretched and no mode change is seen.
        arguments.Should().ContainInOrder("-global", "virtio-vga.xres=2560", "-global", "virtio-vga.yres=1377");
        arguments.Should().ContainInOrder("-fw_cfg", "name=opt/org.dextoros/display,string=2560x1377");
        arguments.Should().ContainInOrder("-fw_cfg", "name=opt/org.dextoros/scale,string=150");
    }

    [Fact]
    public void AComputerWithoutSoundStillStartsTheVmWithASilentCard()
    {
        var executable = Write("qemu-system-x86_64.exe");
        var iso = Write("system.img");
        var data = Write("data.img");
        var request = new SessionStartRequest(Guid.NewGuid(), iso, iso, data, new string('a', 64), 4096, 4);

        var silent = QemuLaunchPlanBuilder.Build(executable, new ResolvedSystemImage(iso, SystemImageLocation.UsbFallback), request, hostAudio: false).Arguments;

        silent.Should().Contain(argument => argument.StartsWith("none,id=", StringComparison.Ordinal));
        silent.Should().NotContain(argument => argument.StartsWith("dsound,", StringComparison.Ordinal));
    }

    [Fact]
    public void DexTorOsCanSignalTheLauncherOverItsOwnPortAndNothingFlowsTheOtherWay()
    {
        var plan = BuildPlan(out _);
        var request = plan.Arguments;

        request.Should().Contain("virtio-serial-pci,id=dextorserial");
        request.Should().Contain(argument => argument.StartsWith("pipe,id=dextorctl,path=dextor-ctl-", StringComparison.Ordinal));
        request.Should().Contain($"virtserialport,bus=dextorserial.0,chardev=dextorctl,name={QemuLaunchPlanBuilder.ControlPortName}");
    }

    private QemuLaunchPlan BuildPlan(out string dataImagePath)
    {
        var executable = Write("qemu-system-x86_64.exe");
        var iso = Write("system.img");
        dataImagePath = Write("data.img");
        var request = new SessionStartRequest(
            Guid.NewGuid(), iso, iso, dataImagePath, new string('a', 64), 4096, 4, "aaaabbbb-cccc-dddd-eeee-ffff00001111");
        return QemuLaunchPlanBuilder.Build(executable, new ResolvedSystemImage(iso, SystemImageLocation.UsbFallback), request);
    }

    private string Write(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
