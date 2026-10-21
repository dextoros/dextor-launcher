using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

using DexTor.Contracts;

namespace DexTor.Provisioner;

public interface IPhysicalDiskInventory
{
    Task<PhysicalDiskSnapshot?> GetAsync(int diskNumber, CancellationToken cancellationToken = default);
}

public interface IDiskPartExecutor
{
    Task<(bool Success, string Detail)> ExecuteAsync(string script, CancellationToken cancellationToken = default);
}

public sealed class PowerShellPhysicalDiskInventory : IPhysicalDiskInventory
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<PhysicalDiskSnapshot?> GetAsync(int diskNumber, CancellationToken cancellationToken = default)
    {
        if (diskNumber < 0)
        {
            return null;
        }

        var command = $"$d=Get-Disk -Number {diskNumber} -ErrorAction Stop;" +
            "[pscustomobject]@{DiskNumber=$d.Number;UniqueId=[string]$d.UniqueId;SerialNumber=[string]$d.SerialNumber;" +
            "CapacityBytes=[long]$d.Size;BusType=$d.BusType.ToString();FriendlyName=[string]$d.FriendlyName;" +
            "IsBoot=[bool]$d.IsBoot;IsSystem=[bool]$d.IsSystem;IsReadOnly=[bool]$d.IsReadOnly;IsOffline=[bool]$d.IsOffline}" +
            "|ConvertTo-Json -Compress";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        return JsonSerializer.Deserialize<PhysicalDiskSnapshot>(output, JsonOptions);
    }
}

public sealed class DiskPartExecutor : IDiskPartExecutor
{
    public async Task<(bool Success, string Detail)> ExecuteAsync(
        string script,
        CancellationToken cancellationToken = default)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"dextor-diskpart-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(scriptPath, script, Encoding.ASCII, cancellationToken).ConfigureAwait(false);
            var diskPartPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "diskpart.exe");
            var startInfo = new ProcessStartInfo(diskPartPath)
            {
                Arguments = $"/s \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, "diskpart.exe could not be started.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            return (process.ExitCode == 0, string.IsNullOrWhiteSpace(error) ? output : error);
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
                // A stale command file contains no secrets and is never reused.
            }
        }
    }
}

/// <summary>Finds the volume on a partition by where the partition starts.</summary>
public interface IDataVolumeLocator
{
    /// <summary>The <c>\\?\Volume{…}\</c> path, or null while Windows has not mounted it.</summary>
    Task<string?> FindAsync(int diskNumber, long partitionOffsetBytes, CancellationToken cancellationToken = default);
}

public sealed class PowerShellDataVolumeLocator : IDataVolumeLocator
{
    public async Task<string?> FindAsync(int diskNumber, long partitionOffsetBytes, CancellationToken cancellationToken = default)
    {
        // By offset, not by number: the offset is what the writer put on disk, while
        // partition numbering is Windows' own bookkeeping.
        var command = $"$ProgressPreference='SilentlyContinue';$p=Get-Partition -DiskNumber {diskNumber} -ErrorAction Stop|Where-Object {{$_.Offset -eq {partitionOffsetBytes}}}|Select-Object -First 1;" +
            @"if($p){$p.AccessPaths|Where-Object {$_ -like '\\?\Volume*'}|Select-Object -First 1}";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoLogo -NoProfile -NonInteractive -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(startInfo);
        if (process is null) return null;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return process.ExitCode == 0 ? ParseVolumePath(output) : null;
    }

    /// <summary>The first volume path in PowerShell's output, ignoring anything around it.</summary>
    internal static string? ParseVolumePath(string output)
    {
        var path = (output ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase));
        return path is null ? null : path.EndsWith('\\') ? path : path + "\\";
    }
}

public sealed class HybridUsbProvisioner(
    IPhysicalDiskInventory diskInventory,
    IDiskPartExecutor diskPartExecutor,
    IHybridDiskWriter hybridDiskWriter,
    Action<UsbProvisioningStage>? stageChanged = null,
    IDataVolumeLocator? volumeLocator = null,
    Action<UsbProvisioningStage, long, long>? bytesChanged = null,
    IVolumeLocker? volumeLocker = null)
{
    private readonly IDataVolumeLocator _volumeLocator = volumeLocator ?? new PowerShellDataVolumeLocator();
    private readonly IVolumeLocker _volumeLocker = volumeLocker ?? new WindowsVolumeLocker();

    private UsbProvisioningStage _stage = UsbProvisioningStage.Validating;

    /// <summary>Reports a stage and remembers it, so a failure can say where it happened.</summary>
    private UsbProvisioningRequest? _journalRequest;

    private void Enter(UsbProvisioningStage stage)
    {
        _stage = stage;
        if (_journalRequest is not null) AppendJournalLine(_journalRequest, $"stage:{stage}");
        stageChanged?.Invoke(stage);
    }

    /// <summary>Releases the volume locks once, whichever path gets there first.</summary>
    private sealed class LockHolder(IDisposable? locks) : IDisposable
    {
        private IDisposable? _locks = locks;

        internal bool Held => _locks is not null;

        public void Dispose()
        {
            _locks?.Dispose();
            _locks = null;
        }
    }

    /// <summary>Bytes moved within a stage, for the stages that take minutes.</summary>
    private Action<long, long>? BytesFor(UsbProvisioningStage stage) =>
        bytesChanged is null ? null : (done, total) => bytesChanged(stage, done, total);

    /// <summary>
    /// How long a device that dropped off the bus is waited for before it is called gone.
    /// </summary>
    /// <remarks>
    /// Long enough for a controller that re-enumerates after <c>clean</c>, short enough
    /// that an operator who has to act is not left watching a progress bar.
    /// </remarks>
    internal static readonly TimeSpan DeviceReturnTimeout = TimeSpan.FromSeconds(20);

    public async Task<UsbProvisioningResult> ExecuteAsync(
        UsbProvisioningRequest request,
        CancellationToken cancellationToken = default)
    {
        var diskChanged = false;
        _journalRequest = request;
        // The first line says which build wrote the journal.
        AppendJournalLine(request, $"provisioner {ProvisionerVersion} on {Environment.OSVersion.VersionString}, user {Environment.UserName}");
        try
        {
            Enter(UsbProvisioningStage.Validating);
            if (!IsAdministrator())
            {
                return Rejected(request, "Administrator privileges are required.");
            }

            var actualDisk = await diskInventory.GetAsync(request.Device.DiskNumber, cancellationToken).ConfigureAwait(false);
            if (actualDisk is null)
            {
                return Rejected(request, "The selected physical disk is no longer available.");
            }

            UsbProvisioningPlanner.ValidateRequest(request, actualDisk);
            var payload = ValidateAllPayloadBeforeDiskChange(request, BytesFor(UsbProvisioningStage.Validating));
            ValidateCapacityForPayload(request, actualDisk, payload);
            // The hybrid image lays out its partition table in 512-byte sectors. A stick
            // that presents 4096-byte sectors refuses those writes with an error that names
            // nothing, after the disk has been touched; it is refused here instead, before.
            if (hybridDiskWriter.GetLogicalSectorSize(actualDisk.DiskNumber) is { } sectorSize
                && sectorSize != HybridGptDiskWriter.SectorSize)
            {
                return Rejected(request,
                    $"This USB uses {sectorSize}-byte sectors. The DexTor OS image needs a drive with 512-byte sectors - most ordinary USB sticks; choose another drive.");
            }

            var dataLetter = FindAvailableDriveLetter();
            var plan = UsbProvisioningPlanner.Build(request, actualDisk, dataLetter);
            SaveJournal(request, "validated");

            SaveJournal(request, "disk-change-started");
            diskChanged = true;
            Enter(UsbProvisioningStage.CleaningDisk);
            // Locked volumes let the image replace the old layout in one pass, so the disk
            // is never left without a partition table for Windows to offer to format.
            // Only when a volume refuses is the stick cleared the old way.
            if (actualDisk.IsReadOnly)
            {
                // Cleared before the volumes are locked. Asked afterwards, diskpart waited on
                // the locked volumes: one run spent 10.5 minutes in "removing the old
                // contents" on this one command, for a disk that was not even read-only.
                var cleared = await diskPartExecutor.ExecuteAsync(plan.ClearReadOnlyScript, cancellationToken).ConfigureAwait(false);
                SaveJournal(request, cleared.Success ? "read-only-cleared" : "read-only-clear-failed", cleared.Detail);
            }

            using var lockedVolumes = new LockHolder(_volumeLocker.TryLockVolumesOn(actualDisk.DiskNumber));
            SaveJournal(request, lockedVolumes.Held ? "volumes-locked" : "volumes-not-lockable:clean");
            var prepareScript = PrepareScriptFor(plan, volumesLocked: lockedVolumes.Held);
            // With the volumes locked there is nothing to clean - the image replaces the
            // layout - so diskpart is not asked at all.
            var prepareResult = lockedVolumes.Held
                ? (Success: true, Detail: "volumes locked; the image replaces the layout")
                : await diskPartExecutor.ExecuteAsync(prepareScript, cancellationToken).ConfigureAwait(false);
            if (!prepareResult.Success)
            {
                // A stick that re-enumerates while being cleared is the common case here,
                // and it comes back within seconds. The retry waits for that, by identity:
                // it never proceeds against whatever else may have taken the disk number.
                var recovered = await TryRecoverDeviceAsync(request, cancellationToken).ConfigureAwait(false);
                if (recovered.Presence == UsbDevicePresence.Present)
                {
                    SaveJournal(request, "disk-clean-retry", prepareResult.Detail);
                    Enter(UsbProvisioningStage.CleaningDisk);
                    prepareResult = await diskPartExecutor
                        .ExecuteAsync(prepareScript, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (!prepareResult.Success)
                {
                    var failure = UsbFailureClassifier.ClassifyDiskPart(recovered, prepareResult.Detail);
                    SaveJournal(request, "interrupted:disk-clean", $"{failure}: {prepareResult.Detail} | {recovered.Detail}");
                    return new UsbProvisioningResult(
                        request.RequestId,
                        UsbProvisioningStatus.InterruptedAfterDiskChange,
                        UsbFailureClassifier.Describe(
                            failure,
                            "Windows could not clear the selected USB. Reconnect it and try again."),
                        Failure: failure);
                }
            }

            SaveJournal(request, "disk-cleaned");
            if (lockedVolumes.Held)
            {
                // Writing straight over the stick's old layout - possibly one an earlier
                // attempt left half-done - failed on one machine until another tool had
                // reinitialized the disk. The layout is reset to an empty GPT first, in one
                // request, so there is never a table-less disk for Windows to prompt about.
                SaveJournal(request, hybridDiskWriter.TryResetPartitionTable(actualDisk.DiskNumber)
                    ? "layout-reset"
                    : "layout-reset-unavailable");
            }
            Enter(UsbProvisioningStage.WritingSystemImage);
            // Only written here; it is read back with the copy for Windows at the end, so all
            // writing comes first and all checking after (one counter each on step 5).
            var imageLength = new FileInfo(request.SystemImagePath).Length;
            var reportWrite = BytesFor(UsbProvisioningStage.WritingSystemImage);
            Task<HybridGptLayout> WriteImageAsync() => hybridDiskWriter.WriteAsync(
                request.SystemImagePath,
                actualDisk.DiskNumber,
                actualDisk.CapacityBytes,
                reportWrite is null ? null : new InlineProgress<long>(done => reportWrite(done, imageLength)),
                verify: false,
                cancellationToken: cancellationToken);
            HybridGptLayout layout;
            try
            {
                layout = await WriteImageAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (lockedVolumes.Held
                && exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                // Writing over locked volumes is refused on some machines. The stick is
                // then cleared the old way and written once more; only if that fails too
                // does creation stop.
                SaveJournal(request, "locked-write-failed:clean-retry", Describe(exception));
                lockedVolumes.Dispose();
                Enter(UsbProvisioningStage.CleaningDisk);
                var cleanResult = await diskPartExecutor.ExecuteAsync(plan.PrepareDiskScript, cancellationToken).ConfigureAwait(false);
                if (!cleanResult.Success)
                {
                    throw new IOException("Windows could not clear the selected USB after writing over its volumes failed. " + cleanResult.Detail, exception);
                }

                Enter(UsbProvisioningStage.WritingSystemImage);
                layout = await WriteImageAsync().ConfigureAwait(false);
            }

            // The new table is on disk and Windows has re-read it; the old volumes are gone.
            lockedVolumes.Dispose();
            SaveJournal(request, "hybrid-image-written");
            plan = UsbProvisioningPlanner.Build(request, actualDisk, dataLetter, layout.DataPartitionEntryIndex);
            Enter(UsbProvisioningStage.FormattingDataVolume);
            var formatResult = await FormatDataPartitionAsync(request, plan.FormatDataPartitionScript, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!formatResult.Success)
            {
                var probe = await UsbDeviceAvailability
                    .ProbeAsync(diskInventory, request.Device, cancellationToken)
                    .ConfigureAwait(false);
                var failure = UsbFailureClassifier.ClassifyDiskPart(probe, formatResult.Detail);
                SaveJournal(request, "interrupted:data-format", $"{failure}: {formatResult.Detail} | {probe.Detail}");
                return new UsbProvisioningResult(
                    request.RequestId,
                    UsbProvisioningStatus.InterruptedAfterDiskChange,
                    UsbFailureClassifier.Describe(
                        failure,
                        "Windows could not create the DexTor data volume. Reconnect the USB and retry creation."),
                    Failure: failure);
            }

            SaveJournal(request, "data-partition-ready");
            // Everything is written through the volume's own path, with no drive letter:
            // a letter makes Explorer and AutoPlay open windows on a half-written drive.
            // The letter is given once, at the very end.
            Enter(UsbProvisioningStage.WaitingForDataVolume);
            var dataRoot = await WaitForVolumePathAsync(
                actualDisk.DiskNumber,
                checked((long)layout.DataPartitionFirstLba * HybridGptDiskWriter.SectorSize),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await WaitForDataVolumeAsync(dataRoot, cancellationToken: cancellationToken).ConfigureAwait(false);
            CopyPayload(request, dataRoot, Enter, cancellationToken, BytesFor(UsbProvisioningStage.CopyingWindowsImage),
                BytesFor(UsbProvisioningStage.CreatingPrivateStorage));
            SaveJournal(request, "verifying-usb");
            Enter(UsbProvisioningStage.Verifying);
            // One check of everything written, read from the stick itself: the raw boot image
            // first, then the copy for Windows - twice the image size on one counter.
            var reportCheck = BytesFor(UsbProvisioningStage.Verifying);
            await hybridDiskWriter.VerifyAsync(
                request.SystemImagePath,
                actualDisk.DiskNumber,
                reportCheck is null ? null : new InlineProgress<long>(done => reportCheck(done, imageLength * 2)),
                cancellationToken).ConfigureAwait(false);
            var verification = UsbPayloadVerifier.Verify(
                dataRoot,
                request.ExpectedSystemImageSha256,
                request.DataImageSizeBytes,
                reportCheck is null ? null : (done, _) => reportCheck(imageLength + done, imageLength * 2),
                cancellationToken);
            var finalDisk = await diskInventory.GetAsync(request.Device.DiskNumber, cancellationToken).ConfigureAwait(false);
            if (finalDisk is null)
            {
                throw new IOException("The USB was disconnected during final verification.");
            }

            UsbProvisioningPlanner.ValidateRequest(request, finalDisk);
            UsbPayloadVerifier.MarkReady(dataRoot, request.RequestId, verification);
            HideServiceDirectory(dataRoot);

            // Only now, with the drive complete and its service folder hidden, does it get
            // a letter - so the one window Windows may open shows an ordinary drive.
            var letterResult = await FormatDataPartitionAsync(request, plan.FinalizeDataPartitionScript, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!letterResult.Success)
            {
                SaveJournal(request, "interrupted:assign-letter", letterResult.Detail);
                return new UsbProvisioningResult(
                    request.RequestId,
                    UsbProvisioningStatus.InterruptedAfterDiskChange,
                    "The DexTor USB was written and verified, but Windows did not give it a drive letter. Reconnect the USB.");
            }

            var letterRoot = $"{plan.DataDriveLetter}:\\";
            await WaitForDataVolumeAsync(letterRoot, cancellationToken: cancellationToken).ConfigureAwait(false);
            SaveJournal(request, "complete");
            Enter(UsbProvisioningStage.Complete);
            return new UsbProvisioningResult(
                request.RequestId,
                UsbProvisioningStatus.Complete,
                "DexTor USB was created and verified.",
                letterRoot,
                Rebase(verification.SystemImagePath, dataRoot, letterRoot),
                Rebase(verification.DataImagePath, dataRoot, letterRoot),
                verification.SystemImageSha256);
        }
        catch (OperationCanceledException)
        {
            SaveJournal(request, diskChanged ? "interrupted:cancelled" : "cancelled-before-disk-change");
            return new UsbProvisioningResult(
                request.RequestId,
                diskChanged
                    ? UsbProvisioningStatus.InterruptedAfterDiskChange
                    : UsbProvisioningStatus.FailedBeforeDiskChange,
                "USB creation was cancelled.");
        }
        catch (Exception exception)
        {
            // Before reporting, ask what the device is doing now. "The device is not
            // ready." on its own tells the operator nothing they can act on; whether the
            // stick is gone, still there, or swapped for another one does.
            var failure = exception is LiveImageBootAssetException
                ? UsbProvisioningFailure.LiveImageBootAssets
                : UsbFailureClassifier.Classify(
                    await ProbeQuietlyAsync(request, cancellationToken).ConfigureAwait(false),
                    exception);
            SaveJournal(request, diskChanged ? "interrupted:error" : "failed-before-disk-change", $"{failure} at {_stage}: {Describe(exception)}");
            return new UsbProvisioningResult(
                request.RequestId,
                diskChanged
                    ? UsbProvisioningStatus.InterruptedAfterDiskChange
                    : UsbProvisioningStatus.FailedBeforeDiskChange,
                UsbFailureClassifier.Describe(failure, $"{exception.Message} ({_stage})"),
                Failure: failure,
                Diagnostic: $"{failure} at {_stage}: {Describe(exception)}",
                FailureOffsetBytes: UsbFailureClassifier.FindDiskWrite(exception)?.Offset);
        }
    }

    /// <summary>
    /// Everything about an exception that tells a support engineer where it came from: a
    /// bare "Incorrect function." from one PC said nothing about which call made it.
    /// </summary>
    internal static string Describe(Exception exception)
    {
        var inner = exception;
        while (inner.InnerException is not null) inner = inner.InnerException;
        var frames = (inner.StackTrace ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(6);
        var win32 = inner is System.ComponentModel.Win32Exception native ? $" win32={native.NativeErrorCode}" : string.Empty;
        return $"{inner.GetType().FullName} hresult=0x{inner.HResult:X8}{win32}: {inner.Message} | {string.Join(" | ", frames)}";
    }

    /// <summary>
    /// With the disk's volumes locked the image replaces the old layout directly and the
    /// stick is not cleaned; only without that lock is <c>clean</c> needed.
    /// </summary>
    internal static string PrepareScriptFor(UsbProvisioningPlan plan, bool volumesLocked) =>
        volumesLocked ? plan.ClearReadOnlyScript : plan.PrepareDiskScript;

    /// <summary>The same file seen through the drive letter instead of the volume path.</summary>
    internal static string Rebase(string path, string fromRoot, string toRoot) =>
        path.StartsWith(fromRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(toRoot, path[fromRoot.Length..].TrimStart('\\'))
            : path;

    /// <summary>Waits for Windows to mount the freshly formatted partition as a volume.</summary>
    internal async Task<string> WaitForVolumePathAsync(
        int diskNumber,
        long partitionOffsetBytes,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxAttempts = 60,
        CancellationToken cancellationToken = default)
    {
        delay ??= Task.Delay;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await _volumeLocator.FindAsync(diskNumber, partitionOffsetBytes, cancellationToken).ConfigureAwait(false) is { } path)
            {
                return path;
            }

            if (attempt + 1 < maxAttempts)
            {
                await delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Windows did not finish mounting the DexTor data volume. Keep the USB connected and retry creation.");
    }

    internal static string ProvisionerVersion { get; } =
        (typeof(HybridUsbProvisioner).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "unknown").Split('+')[0];

    /// <summary>Where each creation attempt records its steps and the raw diskpart output.</summary>
    /// <remarks>
    /// The launcher passes its own folder: the elevated process may run as another
    /// account - an administrator who typed their password into the prompt - whose
    /// temporary folder the person at the screen would never look in.
    /// </remarks>
    public static string JournalDirectory { get; set; } = DexTorStorage.Journal;

    /// <summary>How many times the new data partition is formatted before giving up.</summary>
    internal const int DataFormatAttempts = 4;

    /// <summary>
    /// Formats the data partition, retrying while the same device is still attached.
    /// </summary>
    /// <remarks>
    /// Right after the raw image is written, Windows may still be re-reading the new
    /// partition table: <c>select partition</c> or <c>format</c> then fails although
    /// nothing is wrong with the stick, and a moment later the same script succeeds.
    /// That transient failure was reported as "Windows could not create the DexTor data
    /// volume". Each retry rescans first and runs only against the device identified at
    /// the start; a stick that left or was swapped ends the attempts at once. The output
    /// of every attempt is kept for the journal.
    /// </remarks>
    internal async Task<(bool Success, string Detail)> FormatDataPartitionAsync(
        UsbProvisioningRequest request,
        string script,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        CancellationToken cancellationToken = default)
    {
        delay ??= Task.Delay;
        var attempts = new List<string>();
        for (var attempt = 1; attempt <= DataFormatAttempts; attempt++)
        {
            var result = await diskPartExecutor.ExecuteAsync(script, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                if (attempt > 1)
                {
                    SaveJournal(request, "data-format-recovered", string.Join(" | ", attempts));
                }
                return result;
            }

            attempts.Add($"attempt {attempt}: {Condense(result.Detail)}");
            if (attempt == DataFormatAttempts) break;
            var probe = await TryRecoverDeviceAsync(request, cancellationToken).ConfigureAwait(false);
            if (probe.Presence != UsbDevicePresence.Present) break;
            Enter(UsbProvisioningStage.WaitingForDataVolume);
            await delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
            Enter(UsbProvisioningStage.FormattingDataVolume);
        }

        return (false, string.Join(" | ", attempts));
    }

    private static string Condense(string detail)
    {
        var lines = (detail ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.Length > 0 && !line.StartsWith("Microsoft DiskPart", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("Copyright", StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith("On computer:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(6);
        return string.Join(" / ", lines);
    }

    /// <summary>
    /// Hides the DexTor folder, so the drive looks like an ordinary one in Explorer.
    /// </summary>
    /// <remarks>
    /// Hidden and System together keep it out of view even for someone who shows hidden
    /// files, while the launcher, the guest and a direct boot still open it by path. Not
    /// being able to set the attributes costs looks, not function, so it never fails the
    /// creation that has already been verified.
    /// </remarks>
    internal static void HideServiceDirectory(string dataRoot)
    {
        try
        {
            var directory = new DirectoryInfo(Path.Combine(dataRoot, "DEXTOR"));
            if (!directory.Exists) return;
            directory.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cosmetic only; see the remarks.
        }
    }

    /// <summary>
    /// Waits briefly for the chosen device to come back, reporting the wait to the UI.
    /// </summary>
    internal async Task<UsbDeviceProbe> TryRecoverDeviceAsync(
        UsbProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        var probe = await UsbDeviceAvailability
            .ProbeAsync(diskInventory, request.Device, cancellationToken)
            .ConfigureAwait(false);
        if (probe.Presence != UsbDevicePresence.Absent)
        {
            return probe;
        }

        return await UsbDeviceAvailability.WaitForReturnAsync(
            diskInventory,
            request.Device,
            DeviceReturnTimeout,
            onWaiting: () => Enter(UsbProvisioningStage.WaitingForDevice),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A presence probe that never replaces the failure being reported with its own.
    /// </summary>
    private async Task<UsbDeviceProbe> ProbeQuietlyAsync(
        UsbProvisioningRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await UsbDeviceAvailability
                .ProbeAsync(diskInventory, request.Device, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception probeFailure)
        {
            _ = cancellationToken;
            return new UsbDeviceProbe(UsbDevicePresence.Present, null, probeFailure.Message);
        }
    }

    internal static IsoBootPayloadInfo ValidateAllPayloadBeforeDiskChange(
        UsbProvisioningRequest request,
        Action<long, long>? progress = null)
    {
        if (!File.Exists(request.SystemImagePath))
        {
            throw new InvalidOperationException("The verified DexTor OS image is missing.");
        }

        var imageHash = IsoBootPayloadExtractor.ComputeSha256(request.SystemImagePath, progress);
        if (!string.Equals(imageHash, request.ExpectedSystemImageSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The DexTor OS image hash changed before USB creation.");
        }

        using (var image = File.OpenRead(request.SystemImagePath))
        {
            HybridGptDiskWriter.ValidateHybridBootRecords(image);
        }

        return IsoBootPayloadExtractor.Inspect(request.SystemImagePath);
    }

    private static void ValidateCapacityForPayload(
        UsbProvisioningRequest request,
        PhysicalDiskSnapshot disk,
        IsoBootPayloadInfo payload)
    {
        const long metadataAllowance = 256L * 1024 * 1024;
        var required = checked(payload.IsoSizeBytes * 2 + request.DataImageSizeBytes + metadataAllowance);
        if (disk.CapacityBytes < required)
        {
            throw new InvalidOperationException("The selected USB has too little space for Direct Boot, Windows launch and private storage.");
        }
    }

    private static void CopyPayload(
        UsbProvisioningRequest request,
        string dataRoot,
        Action<UsbProvisioningStage>? stageChanged,
        CancellationToken cancellationToken,
        Action<long, long>? progress = null,
        Action<long, long>? storageProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The storage comes first: it takes seconds, and the copy's read-back and the final
        // check then follow each other, so "checking the write" is one stretch at the end.
        var storageDirectory = Path.Combine(dataRoot, "DEXTOR", "storage");
        Directory.CreateDirectory(storageDirectory);
        stageChanged?.Invoke(UsbProvisioningStage.CreatingPrivateStorage);
        // All of it valid data, so NTFS never zero-fills during the VM session - without
        // writing it whole where the privilege allows; see DataImageAllocator.
        DataImageAllocator.Create(
            Path.Combine(storageDirectory, "data.img"),
            request.DataImageSizeBytes,
            storageProgress,
            cancellationToken);

        var systemDirectory = Path.Combine(dataRoot, "DEXTOR", "system", "slot-a");
        Directory.CreateDirectory(systemDirectory);
        stageChanged?.Invoke(UsbProvisioningStage.CopyingWindowsImage);
        var partialSystemImage = Path.Combine(systemDirectory, "system.img.partial");
        var finalSystemImage = Path.Combine(systemDirectory, "system.img");
        // Written past the Windows cache, so each byte counted is on the stick. It is read
        // back, from the stick, by the final check - which gates the "ready" marker - and not
        // a second time here.
        var imageLength = new FileInfo(request.SystemImagePath).Length;
        UnbufferedFile.Copy(request.SystemImagePath, partialSystemImage, done => progress?.Invoke(done, imageLength), cancellationToken);
        File.Move(partialSystemImage, finalSystemImage, true);
        var metadataDirectory = Path.Combine(dataRoot, "DEXTOR", "metadata");
        Directory.CreateDirectory(metadataDirectory);
        File.WriteAllText(
            Path.Combine(metadataDirectory, "device.json"),
            JsonSerializer.Serialize(new
            {
                state = "verifying",
                format = 3,
                requestId = request.RequestId,
                systemImageSha256 = request.ExpectedSystemImageSha256,
                storageState = "storage_not_initialized"
            }));
    }

    internal static async Task WaitForDataVolumeAsync(
        string dataRoot,
        Func<string, bool>? readinessProbe = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxAttempts = 60,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        readinessProbe ??= IsDataVolumeReady;
        delay ??= Task.Delay;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (readinessProbe(dataRoot)) return;
            if (attempt + 1 < maxAttempts)
            {
                await delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new IOException("Windows did not finish mounting the DexTor data volume. Keep the USB connected and retry creation.");
    }

    private static bool IsDataVolumeReady(string dataRoot)
    {
        try
        {
            // A volume path has no DriveInfo; whether the root opens and takes a write is
            // the readiness that matters for both kinds of root.
            if (!Directory.Exists(dataRoot)) return false;
            var probe = Path.Combine(dataRoot, $".dextor-mount-{Guid.NewGuid():N}.tmp");
            try
            {
                using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                stream.WriteByte(0x5a);
                DurableFlush.Flush(stream);
                return true;
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static char FindAvailableDriveLetter()
    {
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();
        var available = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(value => (char)value)
            .Where(letter => !used.Contains(letter))
            .Reverse()
            .FirstOrDefault();
        if (available == default)
        {
            throw new InvalidOperationException("A temporary drive letter is required.");
        }

        return available;
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static UsbProvisioningResult Rejected(UsbProvisioningRequest request, string message) =>
        new(request.RequestId, UsbProvisioningStatus.Rejected, message);

    private static void AppendJournalLine(UsbProvisioningRequest request, string line)
    {
        try
        {
            Directory.CreateDirectory(JournalDirectory);
            File.AppendAllText(Path.Combine(JournalDirectory, $"{request.RequestId:N}.log"), $"{DateTimeOffset.UtcNow:O} {line}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Diagnostics only.
        }
    }

    private static void SaveJournal(UsbProvisioningRequest request, string state, string? diagnosticDetail = null)
    {
        try
        {
            // The portable launcher leaves nothing outside the user's temporary folder,
            // so the journal lives beside the image cache rather than in ProgramData.
            var directory = JournalDirectory;
            Directory.CreateDirectory(directory);
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(
                Path.Combine(directory, $"{request.RequestId:N}.json"),
                JsonSerializer.Serialize(new
                {
                    request.RequestId,
                    state,
                    diagnosticDetail,
                    updatedUtc = now
                }));
            // The .json holds the last state; the .log keeps every step, so a failure can
            // be read together with what led up to it.
            File.AppendAllText(
                Path.Combine(directory, $"{request.RequestId:N}.log"),
                $"{now:O} {state}{(string.IsNullOrWhiteSpace(diagnosticDetail) ? string.Empty : " " + diagnosticDetail.ReplaceLineEndings(" "))}{Environment.NewLine}");
        }
        catch
        {
            // The operation result still reports the authoritative failure.
        }
    }
}
