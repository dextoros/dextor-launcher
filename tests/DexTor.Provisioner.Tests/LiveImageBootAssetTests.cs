using System.Text;

using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Iso9660;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

/// <summary>
/// The kernel and initrd are the ones the image's own boot configuration names.
/// </summary>
/// <remarks>
/// These build real ISO files and read them back through the production reader, because
/// the failure this covers was invisible to a "does this path exist" mock: the image was
/// complete and bootable, and validation refused it over the spelling of two filenames.
/// <para>
/// The layout used here is the one the published DexTor OS image actually has — a Debian
/// live-build image whose kernel and initrd carry the kernel package version — reduced to
/// bytes that are quick to write.
/// </para>
/// </remarks>
public sealed class LiveImageBootAssetTests : IDisposable
{
    private const string Kernel = "vmlinuz-6.12.107+deb13-amd64";
    private const string Initrd = "initrd.img-6.12.107+deb13-amd64";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dextor-live-boot-assets",
        Guid.NewGuid().ToString("N"));

    public LiveImageBootAssetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temporary directory is not a test failure.
        }
    }

    [Fact]
    public void AVersionedKernelAndInitrdAreAcceptedWhenTheBootConfigurationNamesThem()
    {
        // The reported failure: "The Live ISO is missing a required boot file: live/vmlinuz"
        // on an image that boots perfectly and never had a file by that name.
        var iso = BuildIso(
            "versioned.iso",
            Entry("DexTor OS", "/live/" + Kernel, "/live/" + Initrd),
            [Kernel, Initrd]);

        var info = IsoBootPayloadExtractor.Inspect(iso);

        info.KernelPath.Should().Be(@"live\" + Kernel);
        info.InitrdPath.Should().Be(@"live\" + Initrd);
    }

    [Fact]
    public void TheUnversionedLayoutStillWorks()
    {
        var iso = BuildIso(
            "unversioned.iso",
            Entry("DexTor OS", "/live/vmlinuz", "/live/initrd.img"),
            ["vmlinuz", "initrd.img"]);

        var info = IsoBootPayloadExtractor.Inspect(iso);

        info.KernelPath.Should().Be(@"live\vmlinuz");
        info.InitrdPath.Should().Be(@"live\initrd.img");
    }

    [Fact]
    public void RepeatedEntriesNamingTheSamePairAreNotAmbiguous()
    {
        // The published image has four entries — normal, safe mode, diagnostic and an
        // integrity check — all booting the same pair.
        var iso = BuildIso(
            "repeated.iso",
            Entry("DexTor OS", "/live/" + Kernel, "/live/" + Initrd)
            + Entry("DexTor OS (Safe Mode)", "/live/" + Kernel, "/live/" + Initrd, "nosmp noapic")
            + "submenu 'Utilities...' {\n"
            + Entry("Verify integrity", "/live/" + Kernel, "/live/" + Initrd, "verify-checksums")
            + "}\n",
            [Kernel, Initrd]);

        IsoBootPayloadExtractor.Inspect(iso).KernelPath.Should().Be(@"live\" + Kernel);
    }

    [Fact]
    public void TwoDifferentPairsAreRefusedRatherThanChosenBetween()
    {
        var iso = BuildIso(
            "ambiguous.iso",
            Entry("DexTor OS", "/live/" + Kernel, "/live/" + Initrd)
            + Entry("Other kernel", "/live/vmlinuz-6.1.0-38-amd64", "/live/initrd.img-6.1.0-38-amd64"),
            [Kernel, Initrd, "vmlinuz-6.1.0-38-amd64", "initrd.img-6.1.0-38-amd64"]);

        var act = () => IsoBootPayloadExtractor.Inspect(iso);

        act.Should().Throw<LiveImageBootAssetException>().WithMessage("*more than one kernel and initrd pair*");
    }

    [Fact]
    public void AnInitrdTheImageDoesNotContainIsRefused()
    {
        // Nothing is selected by wildcard to make validation pass: the initrd that is in
        // the image is not substituted for the one the configuration names.
        var iso = BuildIso(
            "dangling.iso",
            Entry("DexTor OS", "/live/" + Kernel, "/live/initrd.img-does-not-exist"),
            [Kernel, Initrd]);

        var act = () => IsoBootPayloadExtractor.Inspect(iso);

        act.Should().Throw<LiveImageBootAssetException>().WithMessage("*initrd the image does not contain*");
    }

    [Fact]
    public void ABootConfigurationWithNoKernelAtAllIsRefused()
    {
        var iso = BuildIso(
            "nokernel.iso",
            "menuentry \"Firmware settings\" {\n\tfwsetup\n}\n",
            [Kernel, Initrd]);

        var act = () => IsoBootPayloadExtractor.Inspect(iso);

        act.Should().Throw<LiveImageBootAssetException>().WithMessage("*does not name a kernel and an initrd*");
    }

    [Fact]
    public void AnEntryWithAKernelButNoInitrdContributesNoPair()
    {
        // A memory test entry is not half of a live boot pair.
        var iso = BuildIso(
            "memtest.iso",
            Entry("DexTor OS", "/live/" + Kernel, "/live/" + Initrd)
            + "menuentry \"Memory test\" {\n\tlinux16\t/live/memtest\n}\n",
            [Kernel, Initrd, "memtest"]);

        IsoBootPayloadExtractor.Inspect(iso).KernelPath.Should().Be(@"live\" + Kernel);
    }

    [Fact]
    public void AKernelThatLeavesTheImageIsRefused()
    {
        var iso = BuildIso(
            "escape.iso",
            Entry("DexTor OS", "/live/../../../etc/passwd", "/live/" + Initrd),
            [Kernel, Initrd]);

        var act = () => IsoBootPayloadExtractor.Inspect(iso);

        act.Should().Throw<LiveImageBootAssetException>().WithMessage("*leaves the image*");
    }

    [Fact]
    public void AKernelOnAnotherDeviceIsRefused()
    {
        var iso = BuildIso(
            "device.iso",
            Entry("DexTor OS", "(hd0,1)/live/" + Kernel, "/live/" + Initrd),
            [Kernel, Initrd]);

        var act = () => IsoBootPayloadExtractor.Inspect(iso);

        act.Should().Throw<LiveImageBootAssetException>().WithMessage("*outside the image*");
    }

    [Fact]
    public void AKernelOutsideTheLivePayloadIsRefused()
    {
        var iso = BuildIso(
            "outside-live.iso",
            Entry("DexTor OS", "/boot/grub/grub.cfg", "/live/" + Initrd),
            [Kernel, Initrd]);

        var act = () => IsoBootPayloadExtractor.Inspect(iso);

        act.Should().Throw<LiveImageBootAssetException>().WithMessage("*outside the live payload directory*");
    }

    [Fact]
    public void AnIncludedConfigurationIsFollowedAndACycleTerminates()
    {
        // The published image sources /boot/grub/config.cfg from grub.cfg. A configuration
        // that sources itself, or sources back the file that included it, must stop.
        var iso = BuildIso(
            "included.iso",
            "source /boot/grub/config.cfg\n",
            [Kernel, Initrd],
            new Dictionary<string, string>
            {
                [@"boot\grub\config.cfg"] =
                    "source /boot/grub/config.cfg\nsource /boot/grub/grub.cfg\n"
                    + Entry("DexTor OS", "/live/" + Kernel, "/live/" + Initrd)
            });

        IsoBootPayloadExtractor.Inspect(iso).KernelPath.Should().Be(@"live\" + Kernel);
    }

    [Fact]
    public void AnEmptyKernelIsRefused()
    {
        var iso = BuildIso(
            "empty-kernel.iso",
            Entry("DexTor OS", "/live/" + Kernel, "/live/" + Initrd),
            [Initrd],
            new Dictionary<string, string> { [@"live\" + Kernel] = string.Empty });

        var act = () => IsoBootPayloadExtractor.Inspect(iso);

        act.Should().Throw<LiveImageBootAssetException>().WithMessage("*kernel is empty*");
    }

    [Fact]
    public void TheExtractedBootVolumeCarriesTheVersionedPairTheConfigurationNames()
    {
        var iso = BuildIso(
            "extract-versioned.iso",
            Entry("DexTor OS", "/live/" + Kernel, "/live/" + Initrd),
            [Kernel, Initrd]);
        var output = Path.Combine(_root, "boot-volume-versioned");

        IsoBootPayloadExtractor.ExtractToBootVolume(iso, output);

        // The boot configuration is copied verbatim, so its references have to resolve on
        // the extracted volume under exactly the names it uses.
        File.Exists(Path.Combine(output, "live", Kernel)).Should().BeTrue();
        File.Exists(Path.Combine(output, "live", Initrd)).Should().BeTrue();
        File.ReadAllText(Path.Combine(output, "boot", "grub", "grub.cfg")).Should().Contain(Kernel);
        File.Exists(Path.Combine(output, "EFI", "BOOT", "BOOTX64.EFI")).Should().BeTrue();
    }

    [Fact]
    public void TheExtractedBootVolumeCarriesAnExtensionlessKernelToo()
    {
        // "vmlinuz" has no extension. An enumeration pattern that only matches names with
        // a dot would drop exactly the file the bootloader needs.
        var iso = BuildIso(
            "extract-unversioned.iso",
            Entry("DexTor OS", "/live/vmlinuz", "/live/initrd.img"),
            ["vmlinuz", "initrd.img"]);
        var output = Path.Combine(_root, "boot-volume-unversioned");

        IsoBootPayloadExtractor.ExtractToBootVolume(iso, output);

        File.Exists(Path.Combine(output, "live", "vmlinuz")).Should().BeTrue();
        File.Exists(Path.Combine(output, "live", "initrd.img")).Should().BeTrue();
    }

    private static string Entry(string title, string kernelPath, string initrdPath, string arguments = "boot=live") =>
        "menuentry \"" + title + "\" {\n"
        + "\tlinux\t" + kernelPath + " " + arguments + "\n"
        + "\tinitrd\t" + initrdPath + "\n"
        + "}\n\n";

    /// <summary>Builds a real ISO with the DexTor live layout and the given configuration.</summary>
    private string BuildIso(
        string name,
        string grubConfig,
        string[] liveFiles,
        Dictionary<string, string>? extraFiles = null)
    {
        var isoPath = Path.Combine(_root, name);
        using var efiImage = new MemoryStream();
        using (var fat = FatFileSystem.FormatFloppy(efiImage, FloppyDiskType.HighDensity, "DEXTOR_EFI"))
        {
            fat.CreateDirectory(@"efi\boot");
            using var bootFile = fat.OpenFile(@"efi\boot\bootx64.efi", FileMode.Create);
            bootFile.Write(Encoding.ASCII.GetBytes("efi"));
        }

        var builder = new CDBuilder { VolumeIdentifier = "DexTor OS", UseJoliet = true };
        builder.AddFile("efi.img", efiImage.ToArray());
        builder.AddFile(@"boot\grub\grub.cfg", Encoding.UTF8.GetBytes(grubConfig));
        builder.AddFile(@"live\filesystem.squashfs", Encoding.ASCII.GetBytes("squashfs"));
        foreach (var file in liveFiles)
        {
            builder.AddFile(@"live\" + file, Encoding.ASCII.GetBytes(file));
        }

        foreach (var (path, content) in extraFiles ?? [])
        {
            builder.AddFile(path, Encoding.UTF8.GetBytes(content));
        }

        builder.Build(isoPath);
        return isoPath;
    }
}
