using System;
using System.IO;

namespace DexTor.Launcher;

internal static class LauncherRuntimeState
{
    private const string StateFile = "dextor-launcher-state.txt";
    private static readonly object StateLock = new();
    private const string PrepareRebootKey = "prepare-reboot";

    private static string ResolveStatePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "DexTor", "Launcher", StateFile);
    }

    public static bool HasPrepareRebootHint()
    {
        try
        {
            var path = ResolveStatePath();
            if (!File.Exists(path))
            {
                return false;
            }

            return string.Equals(File.ReadAllText(path), PrepareRebootKey, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void SetPrepareRebootHint()
    {
        try
        {
            var path = ResolveStatePath();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            lock (StateLock)
            {
                File.WriteAllText(path, PrepareRebootKey);
            }
        }
        catch
        {
            // Ignore local state persistence failures to keep app flow unaffected.
        }
    }

    public static void ClearPrepareRebootHint()
    {
        try
        {
            var path = ResolveStatePath();
            lock (StateLock)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
        catch
        {
            // Ignore cleanup errors to keep app resilient.
        }
    }
}
