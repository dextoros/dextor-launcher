using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using DexTor.Contracts;
using DexTor.Creator;

namespace DexTor.Launcher;

public sealed record UsbDriveSnapshot(
    string RootPath,
    string VolumeLabel,
    string FileSystem,
    long? TotalSizeBytes,
    long? AvailableFreeSpaceBytes,
    bool IsReady,
    bool IsWritable,
    UsbPhysicalIdentity? PhysicalIdentity = null,
    UsbAccessSpeedResult? AccessSpeed = null,
    bool IsExternalDisk = false)
{
    public bool NeedsFormatting => !IsReady || string.IsNullOrWhiteSpace(FileSystem);

    public bool CanProvision => PhysicalIdentity is { IsBoot: false, IsSystem: false, IsReadOnly: false }
        || (PhysicalIdentity is null && IsReady && IsWritable);
}

public sealed record UsbAccessSpeedResult(double WriteMegabytesPerSecond, double ReadMegabytesPerSecond)
{
    public const double SlowWriteThreshold = 15;
    public bool IsSlow => WriteMegabytesPerSecond < SlowWriteThreshold;
}

public static class UsbDiscoveryService
{
    public const long RecommendedUsbReservedBytes = 8L * 1024 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, Task<UsbAccessSpeedResult?>> SpeedCache = new(StringComparer.OrdinalIgnoreCase);

    public static string? DetectDextorUsb(string[] removableRootDrives)
    {
        return removableRootDrives
            .Select(NormalizeDriveRootPath)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .FirstOrDefault(IsDexTorDrivePath);
    }

    public static string[] DetectRemovableRootDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Removable)
                .Select(d => NormalizeDriveRootPath(d.RootDirectory.FullName))
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(r => r!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<UsbDriveSnapshot> InspectRemovableDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Removable)
                .Select(TryInspectDrive)
                .Where(snapshot => snapshot is not null)
                .OfType<UsbDriveSnapshot>()
                .OrderBy(snapshot => snapshot.RootPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static async Task<IReadOnlyList<UsbDriveSnapshot>> InspectPhysicalUsbDrivesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string command =
            "$items=@(Get-Disk -ErrorAction Stop|Where-Object {$_.BusType.ToString() -eq 'USB'}|ForEach-Object {" +
            "$d=$_;$p=@(Get-Partition -DiskNumber $d.Number -ErrorAction SilentlyContinue|Where-Object {$_.DriveLetter}|Select-Object -First 1);" +
            "$v=if($p){$p|Get-Volume -ErrorAction SilentlyContinue}else{$null};" +
            "[pscustomobject]@{DiskNumber=[int]$d.Number;UniqueId=[string]$d.UniqueId;SerialNumber=[string]$d.SerialNumber;" +
            "CapacityBytes=[long]$d.Size;BusType=$d.BusType.ToString();FriendlyName=[string]$d.FriendlyName;" +
            "IsBoot=[bool]$d.IsBoot;IsSystem=[bool]$d.IsSystem;IsReadOnly=[bool]$d.IsReadOnly;" +
            "MediaType=[string](Get-CimInstance Win32_DiskDrive -Filter (\"Index=\"+$d.Number) -ErrorAction SilentlyContinue).MediaType;" +
            "RootPath=if($p){[string]$p.DriveLetter+':\\'}else{''};VolumeLabel=if($v){[string]$v.FileSystemLabel}else{''};" +
            "FileSystem=if($v){[string]$v.FileSystem}else{''};FreeBytes=if($v){[long]$v.SizeRemaining}else{$null}}});" +
            "$items|ConvertTo-Json -Compress";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return InspectRemovableDrives();
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return InspectRemovableDrives();
            }

            // Speed is not measured here any more. Waiting for it held the whole list back
            // for seconds per drive; a measurement already made is attached, and a new
            // one is started by the caller once the list is on screen.
            return ParsePhysicalUsbJson(output)
                .Select(snapshot => SpeedKey(snapshot) is { } key
                    && SpeedCache.TryGetValue(key, out var cached)
                    && cached.IsCompletedSuccessfully
                        ? snapshot with { AccessSpeed = cached.Result }
                        : snapshot)
                .ToArray();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return InspectRemovableDrives();
        }
    }

    /// <summary>
    /// Measures a drive once per physical device and remembers the answer.
    /// </summary>
    public static Task<UsbAccessSpeedResult?> MeasureAccessSpeedCachedAsync(
        UsbDriveSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (!snapshot.IsReady || !snapshot.IsWritable || string.IsNullOrWhiteSpace(snapshot.RootPath)
            || SpeedKey(snapshot) is not { } key)
        {
            return Task.FromResult<UsbAccessSpeedResult?>(null);
        }

        var measurement = SpeedCache.GetOrAdd(key, _ => MeasureAccessSpeedAsync(snapshot.RootPath, CancellationToken.None));
        return measurement.WaitAsync(cancellationToken);
    }

    private static string? SpeedKey(UsbDriveSnapshot snapshot)
    {
        var key = snapshot.PhysicalIdentity?.UniqueId;
        if (string.IsNullOrWhiteSpace(key)) key = snapshot.RootPath;
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    /// <summary>
    /// Measures what the drive sustains, not what its caches can absorb in a burst.
    /// </summary>
    /// <remarks>
    /// The earlier probe wrote 8 MB and read it straight back through the Windows file
    /// cache, so the read figure was memory speed (thousands of MB/s on a USB 3 stick) and
    /// the write figure was the controller's burst. Both directions now bypass the cache
    /// with unbuffered, sector-aligned I/O, the first chunk is treated as warm-up, and the
    /// probe runs for a bounded time so a slow stick is still answered in seconds.
    /// </remarks>
    internal static Task<UsbAccessSpeedResult?> MeasureAccessSpeedAsync(
        string rootPath,
        CancellationToken cancellationToken,
        int maximumBytes = 64 * 1024 * 1024,
        TimeSpan? phaseBudget = null)
    {
        if (maximumBytes < 8 * 1024 * 1024 || string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return Task.FromResult<UsbAccessSpeedResult?>(null);
        }

        var budget = phaseBudget ?? TimeSpan.FromSeconds(4);
        return Task.Run(() => MeasureUnbuffered(rootPath, maximumBytes, budget, cancellationToken), cancellationToken);
    }

    private static unsafe UsbAccessSpeedResult? MeasureUnbuffered(
        string rootPath,
        int maximumBytes,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        const int chunkBytes = 4 * 1024 * 1024;
        const int alignment = 4096;
        var probePath = Path.Combine(rootPath, $".dextor-speed-{Guid.NewGuid():N}.tmp");
        var buffer = (byte*)System.Runtime.InteropServices.NativeMemory.AlignedAlloc(chunkBytes, alignment);
        try
        {
            var span = new Span<byte>(buffer, chunkBytes);
            // Incompressible content, so a controller that compresses cannot flatter itself.
            Random.Shared.NextBytes(span);
            long written;
            double writeSeconds;
            using (var output = File.OpenHandle(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       FileOptions.WriteThrough | NoBuffering))
            {
                (written, writeSeconds) = TimeChunks(maximumBytes, chunkBytes, budget,
                    offset => System.IO.RandomAccess.Write(output, new ReadOnlySpan<byte>(buffer, chunkBytes), offset), cancellationToken);
            }

            long read;
            double readSeconds;
            using (var input = File.OpenHandle(probePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                       NoBuffering | FileOptions.SequentialScan))
            {
                var available = written;
                (read, readSeconds) = TimeChunks((int)available, chunkBytes, budget,
                    offset => System.IO.RandomAccess.Read(input, new Span<byte>(buffer, chunkBytes), offset), cancellationToken);
            }

            if (writeSeconds <= 0 || readSeconds <= 0) return null;
            const double megabyte = 1024d * 1024d;
            return new UsbAccessSpeedResult(written / megabyte / writeSeconds, read / megabyte / readSeconds);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.AlignedFree(buffer);
            try { File.Delete(probePath); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Runs whole chunks until the byte or time limit, excluding the first chunk from the
    /// figures: it pays for file allocation and wake-up, not for transfer.
    /// </summary>
    internal static (long Bytes, double Seconds) TimeChunks(
        int maximumBytes,
        int chunkBytes,
        TimeSpan budget,
        Action<long> transferChunk,
        CancellationToken cancellationToken)
    {
        var total = Stopwatch.StartNew();
        var measured = new Stopwatch();
        long counted = 0;
        for (long offset = 0; offset + chunkBytes <= maximumBytes; offset += chunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (offset > 0) measured.Start();
            transferChunk(offset);
            if (offset > 0)
            {
                measured.Stop();
                counted += chunkBytes;
            }

            if (total.Elapsed >= budget && counted > 0) break;
        }

        return (counted, measured.Elapsed.TotalSeconds);
    }

    internal static IReadOnlyList<UsbDriveSnapshot> ParsePhysicalUsbJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        return elements
            .Select(element => JsonSerializer.Deserialize<PhysicalUsbDto>(element.GetRawText(), JsonOptions))
            .Where(item => item is not null)
            .Select(item => item!)
            .Where(item => item.DiskNumber >= 0 && item.CapacityBytes > 0)
            .Select(item => new UsbDriveSnapshot(
                item.RootPath ?? string.Empty,
                item.VolumeLabel ?? string.Empty,
                item.FileSystem ?? string.Empty,
                item.CapacityBytes,
                item.FreeBytes,
                !string.IsNullOrWhiteSpace(item.RootPath) && !string.IsNullOrWhiteSpace(item.FileSystem),
                !item.IsReadOnly,
                new UsbPhysicalIdentity(
                    item.DiskNumber,
                    item.UniqueId ?? string.Empty,
                    item.SerialNumber ?? string.Empty,
                    item.CapacityBytes,
                    item.BusType ?? string.Empty,
                    item.FriendlyName ?? string.Empty,
                    item.IsBoot,
                    item.IsSystem,
                    item.IsReadOnly),
                IsExternalDisk: IsExternalDiskMedia(item.MediaType)))
            .Where(snapshot => snapshot.PhysicalIdentity is { IsBoot: false, IsSystem: false })
            .OrderBy(snapshot => snapshot.PhysicalIdentity!.DiskNumber)
            .ToArray();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class PhysicalUsbDto
    {
        public int DiskNumber { get; init; }
        public string? UniqueId { get; init; }
        public string? SerialNumber { get; init; }
        public long CapacityBytes { get; init; }
        public string? BusType { get; init; }
        public string? FriendlyName { get; init; }
        public bool IsBoot { get; init; }
        public bool IsSystem { get; init; }
        public bool IsReadOnly { get; init; }
        public string? RootPath { get; init; }
        public string? VolumeLabel { get; init; }
        public string? FileSystem { get; init; }
        public long? FreeBytes { get; init; }
        public string? MediaType { get; init; }
    }

    /// <summary>
    /// A USB hard disk or SSD, as Windows describes it ("External hard disk media", "Fixed hard
    /// disk media"), rather than a stick ("Removable Media"). The wizard asks for an explicit
    /// acknowledgement before erasing one: it is far more likely to hold someone's data.
    /// </summary>
    internal static bool IsExternalDiskMedia(string? mediaType) =>
        !string.IsNullOrWhiteSpace(mediaType)
        && (mediaType.Contains("hard disk", StringComparison.OrdinalIgnoreCase)
            || mediaType.Contains("fixed", StringComparison.OrdinalIgnoreCase));

    private static UsbDriveSnapshot? TryInspectDrive(DriveInfo drive)
    {
        try
        {
            var root = NormalizeDriveRootPath(drive.RootDirectory.FullName);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            if (!drive.IsReady)
            {
                return new UsbDriveSnapshot(root, string.Empty, string.Empty, null, null, false, false);
            }

            return new UsbDriveSnapshot(
                root,
                drive.VolumeLabel?.Trim() ?? string.Empty,
                drive.DriveFormat?.Trim() ?? string.Empty,
                drive.TotalSize,
                drive.AvailableFreeSpace,
                true,
                CanCreateOnDrive(root));
        }
        catch
        {
            return null;
        }
    }

    public static string[] BuildCreateCandidates(string[] removableRootDrives, string? dextorRootPath)
    {
        var normalizedExcluded = NormalizeDriveRootPath(dextorRootPath);
        return removableRootDrives
            .Select(NormalizeDriveRootPath)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Where(r => !string.Equals(r, normalizedExcluded, StringComparison.OrdinalIgnoreCase))
            .Where(CanCreateOnDrive)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static bool CanCreateOnDrive(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return false;
        }

        var probeFilePath = Path.Combine(rootPath, $".dextor-probe-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(
                probeFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);

            stream.WriteByte(0);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probeFilePath))
                {
                    File.Delete(probeFilePath);
                }
            }
            catch
            {
                // Ignore cleanup errors in probe mode.
            }
        }
    }

    public static bool HasEnoughSpace(string rootPath, long requiredBytes, long reservedBytes, out long availableBytes, out long freeBytes)
    {
        availableBytes = 0;
        freeBytes = 0;

        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return false;
        }

        try
        {
            var rootInfo = new DriveInfo(rootPath);
            if (!rootInfo.IsReady)
            {
                return false;
            }

            availableBytes = rootInfo.AvailableFreeSpace;
            freeBytes = rootInfo.TotalFreeSpace;
            return rootInfo.TotalSize >= requiredBytes + reservedBytes;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeDriveRootPath(string? rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(rootPath);
            var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (normalized.Length == 2 && normalized[1] == ':')
            {
                return normalized + Path.DirectorySeparatorChar;
            }

            return normalized;
        }
        catch
        {
            return rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    public static bool IsDexTorDrivePath(string rootPath)
    {
        try
        {
            return File.Exists(Path.Combine(rootPath, "DEXTOR", "metadata", "device.json"))
                || File.Exists(Path.Combine(rootPath, "START DEXTOR ON WINDOWS.html"))
                || File.Exists(CreatorActionService.GetTransactionStatePath(rootPath));
        }
        catch
        {
            return false;
        }
    }
}
