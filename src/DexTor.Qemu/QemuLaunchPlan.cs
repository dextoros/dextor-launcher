using DexTor.Contracts;

namespace DexTor.Qemu;

public sealed record QemuLaunchPlan(string ExecutablePath, IReadOnlyList<string> Arguments);

public static class QemuLaunchPlanBuilder
{
    public const string PrivateStorageDriveId = "dextor-data";
    public const string PrivateStorageSerial = "DEXTOR_DATA";
    public const string GuestPrivateStorageByIdPath = "/dev/disk/by-id/virtio-DEXTOR_DATA";

    /// <summary>The I/O thread the private storage disk is served on.</summary>
    public const string PrivateStorageIoThreadId = "dataio";

    /// <summary>The QEMU audio backend the guest's sound card plays through.</summary>
    public const string AudioDeviceId = "dextor-audio";

    /// <summary>The title the operator sees on the virtual machine window.</summary>
    /// <summary>The SMBIOS type 11 OEM string by which DexTor OS knows it runs under the launcher.</summary>
    public const string LauncherOemString = "DEXTOR_LAUNCHER";

    /// <summary>The VM window's title: just "DexTor OS", as the owner decided - no "Virtual Machine", no QEMU hints.</summary>
    public const string VmWindowTitle = "DexTor OS";

    public const string NetworkId = "dextor-net";

    /// <summary>fw_cfg names the guest reads under /sys/firmware/qemu_fw_cfg/by_name.</summary>
    public const string GuestLocaleFwCfgName = "opt/org.dextoros/locale";
    public const string BuildChannelFwCfgName = "opt/org.dextoros/channel";

    /// <summary>The guest's screen, "2560x1377", and the Windows scale, "150" - for GRUB, the splash and the desktop.</summary>
    public const string DisplayFwCfgName = "opt/org.dextoros/display";
    public const string ScaleFwCfgName = "opt/org.dextoros/scale";

    /// <summary>The virtio-serial port DexTor OS tells the launcher on: "frame" once it draws, "desktop" once its desktop is up.</summary>
    public const string ControlPortName = "org.dextoros.launcher.0";

    /// <summary>The named pipe QEMU serves that port on; the launcher connects to it at start.</summary>
    public static string ControlPipeName(Guid sessionId) => $"dextor-ctl-{sessionId:N}";

    public static QemuLaunchPlan Build(
        string executablePath,
        ResolvedSystemImage systemImage,
        SessionStartRequest request,
        QemuCompatibilityProfile? profile = null,
        bool hostAudio = true)
    {
        profile ??= QemuCompatibilityProfile.Compatible;
        ArgumentNullException.ThrowIfNull(systemImage);
        ArgumentNullException.ThrowIfNull(request);
        if (!File.Exists(executablePath))
        {
            throw new VmPrelaunchException("The QEMU executable is missing.");
        }
        if (!File.Exists(systemImage.Path) || !File.Exists(request.DataImagePath))
        {
            throw new VmPrelaunchException("A required VM disk image is missing.");
        }
        if (request.MemoryMiB is < 2048 or > 32768 || request.ProcessorCount is < 1 or > 32)
        {
            throw new VmPrelaunchException("The requested VM resources are outside the supported range.");
        }

        string[] arguments =
        [
            "-accel", "whpx",
            "-machine", "pc",
            "-smp", $"cores={request.ProcessorCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "-m", $"{request.MemoryMiB.ToString(System.Globalization.CultureInfo.InvariantCulture)}M",
            // No boot menu prompt and no splash pause: the operator never chooses a boot
            // device here, and "Press ESC for boot menu" was the first thing they read.
            "-boot", "once=d,menu=off,splash-time=0",
            "-drive", $"file={EscapeDriveValue(systemImage.Path)},if=ide,index=2,media=cdrom,readonly=on,format=raw",
            // The storage disk is served on its own I/O thread. Without one, its requests went
            // through QEMU's main loop under the lock the vCPUs need too: while the host wrote
            // the file to a slow USB stick, the whole VM stood still for 30-50 s at a time.
            // writeback, not unsafe: the guest's flushes must reach the stick (LUKS, ext4).
            // format=raw and serial=DEXTOR_DATA are how the guest finds the disk.
            // werror=enospc, spelled out: a full stick pauses the VM (BLOCK_IO_ERROR nospace)
            // instead of handing the guest an I/O error its storage cannot recover from; the
            // launcher asks for room and resumes. DexTor OS relies on this.
            "-object", $"iothread,id={PrivateStorageIoThreadId}",
            "-drive", $"file={EscapeDriveValue(request.DataImagePath)},if=none,id={PrivateStorageDriveId},format=raw,cache=writeback,aio=threads,werror=enospc",
            "-device", $"virtio-blk-pci,drive={PrivateStorageDriveId},serial={PrivateStorageSerial},iothread={PrivateStorageIoThreadId}",
            // romfile= drops the card's iPXE option ROM: the VM never boots from the
            // network, and its banner was a second raw firmware screen.
            "-netdev", $"user,id={NetworkId}",
            "-device", $"virtio-net-pci,netdev={NetworkId},romfile=",
            "-device", "ich9-usb-ehci1",
            "-device", "usb-tablet",
            // Sound out of the guest, nothing into it: an HDA controller with an output-only
            // codec on the Windows DirectSound backend. Without a card the guest showed only
            // a dummy output. hda-output rather than hda-duplex, so the host microphone is
            // never offered to the guest.
            // SDL rather than DirectSound: QEMU 11's DirectSound backend opens a recording
            // device too, and on a computer without a microphone QEMU did not start at all
            // ("Could not initialize DirectSoundCapture"). SDL opens only what is played.
            // Without any output device the card stays, silent.
            "-audiodev", $"{(hostAudio ? "sdl" : "none")},id={AudioDeviceId}",
            "-device", "intel-hda",
            "-device", $"hda-output,audiodev={AudioDeviceId}",
            "-vga", "virtio",
            // The guest name is what QEMU puts in its own window title; the launcher then
            // keeps that title to exactly this and replaces the icon with DexTor's.
            "-name", VmWindowTitle,
            // An OEM string the guest's GRUB looks for: under the launcher the boot menu is
            // hidden. Before it, GRUB had to guess from a bare "pc" machine.
            "-smbios", $"type=11,value={LauncherOemString}",
            "-display", "sdl,gl=off,grab-mod=rctrl",
            "-qmp", "stdio",
            "-no-reboot",
            .. (profile.CpuModel is { } cpu ? new[] { "-cpu", cpu } : []),
            // The guest's signals to the launcher. QEMU waits for the launcher to connect to
            // the pipe before it starts the guest; the launcher only reads from it.
            "-device", "virtio-serial-pci,id=dextorserial",
            "-chardev", $"pipe,id=dextorctl,path={ControlPipeName(request.SessionId)}",
            "-device", $"virtserialport,bus=dextorserial.0,chardev=dextorctl,name={ControlPortName}",
            .. DisplayArguments(request),
            .. GuestSettingArguments(request)
        ];
        return new QemuLaunchPlan(Path.GetFullPath(executablePath), arguments);
    }

    /// <summary>
    /// The wizard's language and the build channel, as fw_cfg strings. A value that is not
    /// a plain token is left out rather than escaped: QEMU splits options on commas.
    /// </summary>
    private static IEnumerable<string> GuestSettingArguments(SessionStartRequest request)
    {
        foreach (var (name, value) in new[]
        {
            (GuestLocaleFwCfgName, request.GuestLocale),
            (BuildChannelFwCfgName, request.BuildChannel)
        })
        {
            if (IsPlainToken(value))
            {
                yield return "-fw_cfg";
                yield return $"name={name},string={value}";
            }
        }
    }

    /// <summary>
    /// The guest's first mode as the screen it will be shown on: virtio-vga's preferred mode
    /// (EDID), and the same figures for DexTor OS to start GRUB and its splash in.
    /// </summary>
    private static IEnumerable<string> DisplayArguments(SessionStartRequest request)
    {
        if (request.DisplayWidth is not (>= 640 and <= 16384) || request.DisplayHeight is not (>= 480 and <= 16384)) yield break;
        var width = request.DisplayWidth.Value;
        var height = request.DisplayHeight.Value;
        yield return "-global";
        yield return $"virtio-vga.xres={width.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        yield return "-global";
        yield return $"virtio-vga.yres={height.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        yield return "-fw_cfg";
        yield return $"name={DisplayFwCfgName},string={width.ToString(System.Globalization.CultureInfo.InvariantCulture)}x{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (request.DisplayScalePercent is int scale and >= 100 and <= 400)
        {
            yield return "-fw_cfg";
            yield return $"name={ScaleFwCfgName},string={scale.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
    }

    private static bool IsPlainToken(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    private static string EscapeDriveValue(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Contains(',', StringComparison.Ordinal))
        {
            throw new VmPrelaunchException("VM image paths containing commas are not supported.");
        }
        return fullPath.Replace('\\', '/');
    }
}
