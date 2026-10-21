using System.Text;

using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Iso9660;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class IsoBootPayloadExtractorTests
{
    [Fact]
    public void InspectsAndExtractsBootFilesFromHybridLiveIsoContent()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor-iso-extractor-test", Guid.NewGuid().ToString("N"));
        var isoPath = Path.Combine(root, "dextor.iso");
        var output = Path.Combine(root, "boot");
        try
        {
            Directory.CreateDirectory(root);
            using var efiImage = new MemoryStream();
            using (var fat = FatFileSystem.FormatFloppy(efiImage, FloppyDiskType.HighDensity, "DEXTOR_EFI"))
            {
                fat.CreateDirectory(@"efi\boot");
                using var bootFile = fat.OpenFile(@"efi\boot\bootx64.efi", FileMode.Create);
                bootFile.Write(Encoding.ASCII.GetBytes("efi"));
            }

            var builder = new CDBuilder { VolumeIdentifier = "DEXTOR_TEST", UseJoliet = true };
            builder.AddFile("efi.img", efiImage.ToArray());

            // The boot configuration is what names the kernel and initrd, so a fixture
            // standing in for a bootable image has to name them the way a real one does.
            builder.AddFile(
                @"boot\grub\grub.cfg",
                Encoding.UTF8.GetBytes("menuentry \"DexTor OS\" {\n\tlinux\t/live/vmlinuz boot=live\n\tinitrd\t/live/initrd.img\n}\n"));
            builder.AddFile(@"live\vmlinuz", Encoding.ASCII.GetBytes("kernel"));
            builder.AddFile(@"live\initrd.img", Encoding.ASCII.GetBytes("initrd"));
            builder.AddFile(@"live\filesystem.squashfs", Encoding.ASCII.GetBytes("squashfs"));
            builder.Build(isoPath);

            var info = IsoBootPayloadExtractor.Inspect(isoPath);
            info.VolumeLabel.Should().Be("DEXTOR_TEST");

            IsoBootPayloadExtractor.ExtractToBootVolume(isoPath, output);
            File.ReadAllText(Path.Combine(output, "EFI", "BOOT", "BOOTX64.EFI")).Should().Be("efi");
            var extracted = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
            extracted.Should().Contain(path => path.EndsWith(Path.Combine("boot", "grub", "grub.cfg"), StringComparison.OrdinalIgnoreCase));
            extracted.Should().Contain(path => path.EndsWith(Path.Combine("live", "filesystem.squashfs"), StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public void RejectsIsoWithoutRequiredLiveBootStructure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dextor-invalid-{Guid.NewGuid():N}.iso");
        try
        {
            var builder = new CDBuilder { VolumeIdentifier = "INVALID" };
            builder.AddFile("readme.txt", Encoding.UTF8.GetBytes("not bootable"));
            builder.Build(path);

            var action = () => IsoBootPayloadExtractor.Inspect(path);
            action.Should().Throw<InvalidOperationException>().WithMessage("*required boot file*");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
