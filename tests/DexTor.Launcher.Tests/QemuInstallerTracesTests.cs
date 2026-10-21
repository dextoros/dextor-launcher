using System.Text;

using FluentAssertions;
using Microsoft.Win32;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class QemuInstallerTracesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-traces-" + Guid.NewGuid().ToString("N"));
    private readonly string _hivePath = @"Software\DexTorTests\" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _hive;

    public QemuInstallerTracesTests()
    {
        Directory.CreateDirectory(_root);
        _hive = Registry.CurrentUser.CreateSubKey(_hivePath);
    }

    public void Dispose()
    {
        _hive.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_hivePath, throwOnMissingSubKey: false);
        Directory.Delete(_root, true);
    }

    private string Owned => Path.Combine(_root, "DexTor", "Runtime");

    /// <summary>A stand-in .lnk carrying its target the way a real one does, as UTF-16.</summary>
    private static string Shortcut(string folder, string name, string target)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, [.. "L\0\0\0"u8.ToArray(), .. Encoding.Unicode.GetBytes(target), 0, 0]);
        return path;
    }

    private void Key(string path, string valueName, string value)
    {
        using var key = _hive.CreateSubKey(path);
        key.SetValue(valueName, value);
    }

    [Fact]
    public void ShortcutsIntoOurRuntimeGoAndTheEmptyQemuFolderWithThem()
    {
        var folder = Path.Combine(_root, "Programs", "QEMU");
        Shortcut(folder, "Uninstall.lnk", Path.Combine(Owned, "qemu-11.1.0.staging-1", "qemu-uninstall.exe"));
        Shortcut(folder, "User documentation.lnk", Path.Combine(Owned, "qemu-11.1.0", "share", "doc", "index.html"));

        QemuInstallerTraces.Remove(Owned, [folder], []).Should().Be(2);

        Directory.Exists(folder).Should().BeFalse();
    }

    [Fact]
    public void AQemuThePersonInstalledThemselvesIsNeverTouched()
    {
        var folder = Path.Combine(_root, "Programs", "QEMU");
        var theirs = Shortcut(folder, "Uninstall.lnk", @"C:\Program Files\qemu\qemu-uninstall.exe");
        Key(@"SOFTWARE\QEMU", "Install_Dir", @"C:\Program Files\qemu");
        Key(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QEMU", "UninstallString", "\"C:\\Program Files\\qemu\\qemu-uninstall.exe\"");

        QemuInstallerTraces.Remove(Owned, [folder], [_hive]).Should().Be(0);

        File.Exists(theirs).Should().BeTrue();
        _hive.OpenSubKey(@"SOFTWARE\QEMU").Should().NotBeNull();
        _hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QEMU").Should().NotBeNull();
    }

    [Fact]
    public void RegistryEntriesPointingIntoOurRuntimeAreRemoved()
    {
        Key(@"SOFTWARE\QEMU", "Install_Dir", Path.Combine(Owned, "qemu-11.1.0.staging"));
        Key(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QEMU", "UninstallString",
            "\"" + Path.Combine(Owned, "qemu-11.1.0.staging", "qemu-uninstall.exe") + "\"");

        QemuInstallerTraces.Remove(Owned, [], [_hive]).Should().Be(2);

        _hive.OpenSubKey(@"SOFTWARE\QEMU").Should().BeNull();
        _hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QEMU").Should().BeNull();
    }

    [Fact]
    public void OnlyOurShortcutsLeaveASharedFolderLikeTheDesktop()
    {
        var desktop = Path.Combine(_root, "Desktop");
        var ours = Shortcut(desktop, "QEMU.lnk", Path.Combine(Owned, "qemu-11.1.0", "qemu-system-x86_64.exe"));
        var other = Shortcut(desktop, "Notes.lnk", @"C:\Windows\notepad.exe");

        QemuInstallerTraces.Remove(Owned, [desktop], []);

        File.Exists(ours).Should().BeFalse();
        File.Exists(other).Should().BeTrue();
        Directory.Exists(desktop).Should().BeTrue("only a folder named QEMU is ever removed");
    }

    [Fact]
    public void TheInstallerRunsWithTheLaunchersOwnRightsInsteadOfDemandingElevation()
    {
        var startInfo = QemuRuntimePreparationService.BuildInstallerStartInfo(
            @"C:\Temp\DexTor\Runtime\qemu-w64-11.1.0.exe", @"C:\Temp\DexTor\Runtime\qemu-11.1.0.staging-1");

        startInfo.UseShellExecute.Should().BeFalse("shell execution would honour the installer's demand for elevation");
        startInfo.Environment["__COMPAT_LAYER"].Should().Be("RunAsInvoker");
        startInfo.WorkingDirectory.Should().Be(@"C:\Temp\DexTor\Runtime");
        startInfo.ArgumentList.Should().Equal("/S", @"/D=C:\Temp\DexTor\Runtime\qemu-11.1.0.staging-1");
    }
}
