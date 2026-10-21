using DexTor.Contracts;

namespace DexTor.Launcher;

/// <summary>What one cleanup pass removed.</summary>
internal sealed record TemporaryCacheCleanupReport(int FilesRemoved, int DirectoriesRemoved, long BytesFreed);

/// <summary>
/// Removes what an interrupted run left in <c>%TEMP%\DexTor</c>.
/// </summary>
/// <remarks>
/// A launcher that is killed, loses power or is closed from Task Manager cannot tidy up
/// after itself: abandoned QEMU unpack folders, partial runtime installers and request
/// folders of the elevated provisioner stayed behind for good. The launcher runs as a
/// single instance, so at start nothing of ours can be in progress and all of that is
/// garbage. The partial download of the image is kept on purpose: the next download
/// resumes it. Partial and finished images of other versions are removed once the
/// current version is known (<see cref="RemoveOtherImages"/>). Anything still open by
/// someone is skipped, never forced.
/// </remarks>
internal static class TemporaryCacheCleaner
{
    internal static readonly TimeSpan ProvisioningRequestAge = TimeSpan.FromHours(1);
    internal static readonly TimeSpan JournalAge = TimeSpan.FromDays(30);
    internal const int JournalsKept = 20;

    private static readonly string[] PartialPatterns = ["*.tmp", "*.partial"];

    internal static TemporaryCacheCleanupReport Clean(string dextorRoot, DateTime nowUtc)
    {
        var tally = new Tally();
        if (string.IsNullOrWhiteSpace(dextorRoot) || !Directory.Exists(dextorRoot)) return tally.Report;

        foreach (var cache in new[] { DexTorStorage.LiveImageFolder, "Runtime", "Release" })
        {
            var directory = Path.Combine(dextorRoot, cache);
            foreach (var pattern in PartialPatterns)
            {
                foreach (var file in SafeFiles(directory, pattern))
                {
                    // A partial image is resumed by the next download, not thrown away;
                    // partial images of other versions go in RemoveOtherImages.
                    if (file.EndsWith(".iso.tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    tally.DeleteFile(file);
                }
            }
        }

        var runtime = Path.Combine(dextorRoot, "Runtime");
        var currentRuntime = $"qemu-{QemuRuntimePreparationService.Version}";
        foreach (var directory in SafeDirectories(runtime, "qemu-*"))
        {
            var name = Path.GetFileName(directory);
            if (name.Contains(".staging-", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(name, currentRuntime, StringComparison.OrdinalIgnoreCase))
            {
                tally.DeleteDirectory(directory);
            }
        }

        var currentInstaller = $"qemu-w64-{QemuRuntimePreparationService.Version}.exe";
        foreach (var installer in SafeFiles(runtime, "qemu-w64-*.exe"))
        {
            if (!string.Equals(Path.GetFileName(installer), currentInstaller, StringComparison.OrdinalIgnoreCase))
            {
                tally.DeleteFile(installer);
            }
        }

        var provisioning = Path.Combine(dextorRoot, "Provisioning");
        foreach (var request in SafeDirectories(provisioning, "*"))
        {
            if (Guid.TryParseExact(Path.GetFileName(request), "N", out _)
                && nowUtc - Directory.GetLastWriteTimeUtc(request) > ProvisioningRequestAge)
            {
                tally.DeleteDirectory(request);
            }
        }

        var journals = SafeFiles(Path.Combine(provisioning, "journal"), "*.json")
            .Select(path => (Path: path, Written: File.GetLastWriteTimeUtc(path)))
            .OrderByDescending(journal => journal.Written)
            .ToArray();
        for (var index = 0; index < journals.Length; index++)
        {
            // A journal is the only record of why a creation failed, so the recent ones
            // stay even when old; only what is both old and beyond the kept count goes.
            if (index >= JournalsKept && nowUtc - journals[index].Written > JournalAge)
            {
                tally.DeleteFile(journals[index].Path);
            }
        }

        return tally.Report;
    }

    /// <summary>
    /// Removes image files of other versions, finished or partial, once the current one is known.
    /// </summary>
    internal static TemporaryCacheCleanupReport RemoveOtherImages(string imageCacheRoot, string currentFileName)
    {
        var tally = new Tally();
        string[] keep = [currentFileName, currentFileName + ".tmp", currentFileName + ".tmp.source"];
        foreach (var pattern in new[] { "*.iso", "*.iso.tmp", "*.iso.tmp.source" })
        {
            foreach (var image in SafeFiles(imageCacheRoot, pattern))
            {
                if (!keep.Contains(Path.GetFileName(image), StringComparer.OrdinalIgnoreCase))
                {
                    tally.DeleteFile(image);
                }
            }
        }

        return tally.Report;
    }

    /// <summary>
    /// Moves what an earlier launcher kept in %TEMP%\DexTor to the new cache root, then
    /// clears the old folder.
    /// </summary>
    /// <remarks>
    /// The verified image - 2 GB that would otherwise be downloaded again - and its partial
    /// download are carried over, and so are the creation journals. A runtime unpacked by an
    /// earlier launcher has no file list to be checked against and is downloaded again
    /// anyway, so it is not moved. Whatever cannot be moved or deleted now - a file still
    /// open somewhere - is tried again at the next start.
    /// </remarks>
    private static readonly string[] ImagePatterns = ["*.iso", "*.iso.tmp", "*.iso.tmp.source"];

    /// <summary>
    /// Moves the image from the folder launchers before 0.1.39 used (LiveImage) to
    /// <c>cache</c>, so the 2 GB already verified are not downloaded again.
    /// </summary>
    internal static void MigrateImageFolder(string dextorRoot)
    {
        var legacy = Path.Combine(dextorRoot, DexTorStorage.LegacyLiveImageFolder);
        if (!Directory.Exists(legacy)) return;
        MoveFiles(legacy, Path.Combine(dextorRoot, DexTorStorage.LiveImageFolder), ImagePatterns);
        try
        {
            Directory.Delete(legacy, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Something there is still open; the next start clears it.
        }
    }

    internal static void MigrateFromTemp(string legacyRoot, string newRoot)
    {
        if (string.IsNullOrWhiteSpace(legacyRoot) || !Directory.Exists(legacyRoot)) return;
        if (string.Equals(Path.GetFullPath(legacyRoot), Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase)) return;

        MoveFiles(Path.Combine(legacyRoot, DexTorStorage.LegacyLiveImageFolder), Path.Combine(newRoot, DexTorStorage.LiveImageFolder), ImagePatterns);
        MoveFiles(Path.Combine(legacyRoot, "Provisioning", "journal"), Path.Combine(newRoot, "Provisioning", "journal"), "*.json", "*.log");

        try
        {
            Directory.Delete(legacyRoot, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Something there is still open; the next start clears it.
        }
    }

    private static void MoveFiles(string from, string to, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            foreach (var file in SafeFiles(from, pattern))
            {
                try
                {
                    var destination = Path.Combine(to, Path.GetFileName(file));
                    if (File.Exists(destination)) continue; // the new location already has its own
                    Directory.CreateDirectory(to);
                    File.Move(file, destination);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Left where it is; nothing depends on the move.
                }
            }
        }
    }
    private static string[] SafeFiles(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string[] SafeDirectories(string directory, string pattern)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetDirectories(directory, pattern, SearchOption.TopDirectoryOnly) : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private sealed class Tally
    {
        private int _files;
        private int _directories;
        private long _bytes;

        internal TemporaryCacheCleanupReport Report => new(_files, _directories, _bytes);

        internal void DeleteFile(string path)
        {
            try
            {
                var length = new FileInfo(path).Length;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                _files++;
                _bytes += length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Still open by something: it is left for the next start.
            }
        }

        internal void DeleteDirectory(string path)
        {
            try
            {
                var length = new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(file => file.Length);
                Directory.Delete(path, recursive: true);
                _directories++;
                _bytes += length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Partly removed or in use: whatever is left goes on the next start.
            }
        }
    }
}
