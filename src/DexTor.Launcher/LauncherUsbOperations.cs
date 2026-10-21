using System;
using System.Diagnostics;
using System.IO;

namespace DexTor.Launcher;

public static class LauncherUsbOperations
{
    public static readonly string StartHtmlFile = "START DEXTOR ON WINDOWS.html";

    public static (bool Success, string? ErrorMessage, string? ErrorSolutionKey) TryOpenDexTorHelp(string? dextorRootPath)
    {
        if (string.IsNullOrWhiteSpace(dextorRootPath))
        {
            return (false, "launcher.action.open.error.no_usb", "launcher.action.open.solution.no_usb");
        }

        if (!TryNormalizePath(dextorRootPath, out var normalizedRoot))
        {
            return (false, "launcher.action.open.error.not_found", "launcher.action.open.solution");
        }

        var htmlPath = Path.Combine(normalizedRoot, StartHtmlFile);
        if (File.Exists(htmlPath))
        {
            return TryOpenPath(htmlPath)
                ? (true, null, null)
                : (false, "launcher.action.open.error.open_failed", "launcher.action.open.solution");
        }

        return Directory.Exists(normalizedRoot)
            ? (TryOpenPath(normalizedRoot)
                ? (true, null, null)
                : (false, "launcher.action.open.error.open_failed", "launcher.action.open.solution"))
            : (false, "launcher.action.open.error.not_found", "launcher.action.open.solution");
    }

    public static (bool Success, string? ErrorMessage, string? ErrorSolutionKey) TryStartEjectFlow(string? dextorRootPath)
    {
        if (string.IsNullOrWhiteSpace(dextorRootPath))
        {
            return (false, "launcher.action.eject.error.no_usb", "launcher.action.eject.solution.no_usb");
        }

        if (!TryNormalizePath(dextorRootPath, out var normalizedRoot))
        {
            return (false, "launcher.action.eject.error.not_found", "launcher.action.eject.solution");
        }

        if (!Directory.Exists(normalizedRoot))
        {
            return (false, "launcher.action.eject.error.not_found", "launcher.action.eject.solution.no_usb");
        }

        if (!TryOpenPath(normalizedRoot))
        {
            return (false, "launcher.action.eject.error.open_failed", "launcher.action.eject.solution");
        }

        return (true, "launcher.action.eject.result.manual_eject_opened", null);
    }

    private static bool TryOpenPath(string path)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            };

            _ = Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = Path.GetFullPath(path);
            return true;
        }
        catch
        {
            normalizedPath = string.Empty;
            return false;
        }
    }
}
