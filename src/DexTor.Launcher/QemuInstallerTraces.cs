using System.Text;

using Microsoft.Win32;

namespace DexTor.Launcher;

/// <summary>
/// Removes what the QEMU installer registers on the PC when it is used only to unpack
/// the runtime into DexTor's temporary folder.
/// </summary>
/// <remarks>
/// The official QEMU for Windows is an NSIS installer, and DexTor runs it silently with
/// the destination inside <c>%TEMP%\DexTor\Runtime</c>. Unpacking is all that is wanted,
/// but the installer also adds Start menu shortcuts, and, when it happens to run with
/// administrator rights, an "Apps and features" entry and a machine-wide registry key -
/// all pointing into that temporary folder. For a launcher that installs nothing they
/// are litter, and a stale uninstall entry is worse than litter.
///
/// Only traces that point inside <paramref name="ownedRoot"/> of the methods are removed.
/// A QEMU the person installed themselves lives elsewhere and is never touched.
/// </remarks>
internal static class QemuInstallerTraces
{
    internal const string ShortcutFolderName = "QEMU";

    internal static readonly string[] DefaultShortcutFolders =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ShortcutFolderName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ShortcutFolderName),
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    ];

    /// <summary>The registry keys the installer writes, relative to a hive root.</summary>
    internal static readonly (string Path, string ValueName)[] RegistryKeys =
    [
        (@"SOFTWARE\QEMU", "Install_Dir"),
        (@"SOFTWARE\WOW6432Node\QEMU", "Install_Dir"),
        (@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QEMU", "UninstallString"),
        (@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QEMU", "UninstallString")
    ];

    /// <summary>Removes the installer's traces on this PC that point into <paramref name="ownedRoot"/>.</summary>
    internal static int Remove(string ownedRoot) =>
        Remove(ownedRoot, DefaultShortcutFolders, [Registry.CurrentUser, Registry.LocalMachine]);

    internal static int Remove(string ownedRoot, IEnumerable<string> shortcutFolders, IEnumerable<RegistryKey> hives)
    {
        if (string.IsNullOrWhiteSpace(ownedRoot)) return 0;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ownedRoot));
        var removed = 0;
        foreach (var folder in shortcutFolders.Where(folder => !string.IsNullOrWhiteSpace(folder)))
        {
            removed += RemoveShortcuts(folder, root);
        }

        foreach (var hive in hives)
        {
            foreach (var (path, valueName) in RegistryKeys)
            {
                removed += RemoveKeyIfOwned(hive, path, valueName, root) ? 1 : 0;
            }
        }

        return removed;
    }

    private static int RemoveShortcuts(string folder, string root)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            foreach (var shortcut in Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                if (!PointsInto(shortcut, root)) continue;
                File.Delete(shortcut);
                removed++;
            }

            // The installer's own folder goes once it holds nothing of anyone else's.
            if (string.Equals(Path.GetFileName(folder), ShortcutFolderName, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(folder)
                && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An all-users folder needs rights this process may not have; it is retried
            // the next time the launcher starts with them.
        }

        return removed;
    }

    /// <summary>
    /// Whether a shortcut's target lies inside the owned folder.
    /// </summary>
    /// <remarks>
    /// A .lnk stores its target path as UTF-16 and usually also in the system code page;
    /// finding the owned folder in either is enough, and avoids the shell COM objects.
    /// </remarks>
    internal static bool PointsInto(string shortcutPath, string root)
    {
        var bytes = File.ReadAllBytes(shortcutPath);
        return Contains(bytes, Encoding.Unicode.GetBytes(root))
            || Contains(bytes, Encoding.Unicode.GetBytes(root.ToUpperInvariant()))
            || Contains(bytes, Encoding.Latin1.GetBytes(root));
    }

    private static bool Contains(byte[] haystack, byte[] needle) =>
        needle.Length > 0 && haystack.AsSpan().IndexOf(needle) >= 0;

    private static bool RemoveKeyIfOwned(RegistryKey hive, string path, string valueName, string root)
    {
        try
        {
            string? value;
            using (var key = hive.OpenSubKey(path))
            {
                if (key is null) return false;
                value = key.GetValue(valueName) as string;
            }

            if (string.IsNullOrWhiteSpace(value)
                || value.Trim('"').IndexOf(root, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            hive.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // HKLM needs administrator rights; without them the installer could not have
            // written there either, and an old entry is removed by a later elevated start.
            return false;
        }
    }
}
