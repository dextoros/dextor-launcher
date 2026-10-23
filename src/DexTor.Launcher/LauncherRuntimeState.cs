using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DexTor.Application;

namespace DexTor.Launcher;

/// <summary>
/// The launcher's small state between runs, one entry per line: the "prepare-reboot" hint
/// and the language the operator picked ("language=ru").
/// </summary>
/// <remarks>
/// A file holding only the hint is exactly what earlier builds wrote and read, so either
/// build understands the other's file. Every failure is swallowed: this state is a
/// convenience and must never stop the launcher.
/// </remarks>
internal static class LauncherRuntimeState
{
    private const string StateFile = "dextor-launcher-state.txt";
    private static readonly object StateLock = new();
    private const string PrepareRebootKey = "prepare-reboot";
    private const string LanguagePrefix = "language=";

    private static string ResolveStatePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("DEXTOR_LAUNCHER_STATE_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Path.Combine(DexTor.Contracts.DexTorStorage.Root, "Launcher", StateFile);
    }

    public static bool HasPrepareRebootHint()
    {
        try
        {
            return ReadEntries().Contains(PrepareRebootKey, StringComparer.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    public static void SetPrepareRebootHint() =>
        Update(entries =>
        {
            if (!entries.Contains(PrepareRebootKey, StringComparer.Ordinal)) entries.Insert(0, PrepareRebootKey);
        });

    public static void ClearPrepareRebootHint() =>
        Update(entries => entries.RemoveAll(entry => string.Equals(entry, PrepareRebootKey, StringComparison.Ordinal)));

    /// <summary>
    /// The language the operator last picked, or null when none was stored or the stored one
    /// is not a language the launcher has.
    /// </summary>
    public static string? ReadLanguage()
    {
        try
        {
            var stored = ReadEntries()
                .LastOrDefault(entry => entry.StartsWith(LanguagePrefix, StringComparison.Ordinal))?[LanguagePrefix.Length..].Trim();
            return stored is not null && LocaleCatalog.SupportedLanguages.Contains(stored, StringComparer.OrdinalIgnoreCase)
                ? LocaleCatalog.GetSupportedLanguage(stored)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The language to start in: the one the operator picked, else Windows' own.</summary>
    public static string ResolveStartLanguage() => ReadLanguage() ?? Localizer.ResolveLanguage();

    /// <summary>Remembers the operator's pick for the next start.</summary>
    public static void SaveLanguage(string language)
    {
        if (!LocaleCatalog.SupportedLanguages.Contains(language, StringComparer.OrdinalIgnoreCase)) return;
        var code = LocaleCatalog.GetSupportedLanguage(language);
        Update(entries =>
        {
            entries.RemoveAll(entry => entry.StartsWith(LanguagePrefix, StringComparison.Ordinal));
            entries.Add(LanguagePrefix + code);
        });
    }

    private static List<string> ReadEntries()
    {
        var path = ResolveStatePath();
        if (!File.Exists(path)) return [];
        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();
    }

    private static void Update(Action<List<string>> change)
    {
        try
        {
            var path = ResolveStatePath();
            lock (StateLock)
            {
                var entries = ReadEntries();
                change(entries);
                if (entries.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(path, string.Join("\n", entries));
            }
        }
        catch
        {
            // Ignore local state persistence failures to keep app flow unaffected.
        }
    }
}
