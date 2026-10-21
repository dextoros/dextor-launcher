using System.Diagnostics;

using DexTor.Contracts;
using DexTor.Creator;

namespace DexTor.Launcher;

internal static class UsbProvisionerClient
{
    public static async Task<CreatorActionService.CreateUsbResult> CreateAsync(
        UsbDriveSnapshot usb,
        string systemImagePath,
        string systemImageSha256,
        IProgress<CreatorActionService.CreatorStepProgress> progress)
    {
        var transaction = CreatorActionService.CreateTransaction();
        if (usb.PhysicalIdentity is not { } identity)
        {
            return Failure("The selected USB has no physical device identity.", "USB");
        }

        var displayPath = !string.IsNullOrWhiteSpace(usb.RootPath)
            ? usb.RootPath
            : $"PhysicalDrive{identity.DiskNumber}";
        if (!File.Exists(systemImagePath))
        {
            return Failure("The verified DexTor OS image is missing.", displayPath, "launcher.create.error.release");
        }

        if (!UsbProvisioningCapacity.TryCalculateDataImageSize(
                identity.CapacityBytes,
                new FileInfo(systemImagePath).Length,
                out var dataImageSize))
        {
            return Failure("The selected USB does not have enough capacity for DexTor OS.", displayPath);
        }

        var request = new UsbProvisioningRequest(
            Guid.NewGuid(),
            identity,
            systemImagePath,
            systemImageSha256,
            dataImageSize,
            true);
        var tempDirectory = Path.Combine(DexTorStorage.Provisioning, request.RequestId.ToString("N"));
        var requestPath = Path.Combine(tempDirectory, "request.json");
        var resultPath = Path.Combine(tempDirectory, "result.json");
        var progressPath = Path.Combine(tempDirectory, "progress.json");
        var cancelPath = Path.Combine(tempDirectory, "cancel");
        // The journal lives in the launcher user's own temporary folder, whoever the
        // elevated process runs as, so it is where the person at the screen will look.
        var journalPath = Path.Combine(JournalDirectory, $"{request.RequestId:N}.log");
        try
        {
            Directory.CreateDirectory(tempDirectory);
            await File.WriteAllTextAsync(requestPath, UsbProvisioningSerializer.SerializeRequest(request));
            progress.Report(new CreatorActionService.CreatorStepProgress(CreatorStep.FormatUsb, CreatorActionService.CreatorStepStatus.Info));
            s_cancelRequested = false;
            s_activeCancelPath = cancelPath;
            using var process = Process.Start(BuildStartInfo(requestPath, resultPath, progressPath, journalDirectory: JournalDirectory, cancelPath: cancelPath));
            if (process is null)
            {
                return Failure("DexTor Provisioner could not be started.", displayPath);
            }

            var reachedStage = await MonitorProgressAsync(process, progressPath, request.RequestId, progress);
            if (!File.Exists(resultPath))
            {
                // The provisioner is gone and left no result. What it did before that is
                // not known here, so it is not claimed: an unheard-of outcome after the
                // elevated process started is never reported as "nothing was written".
                return Failure(
                    "DexTor Provisioner did not return a result.",
                    displayPath,
                    errorSafetyMessageKey: SafetyKeyForLostOutcome(reachedStage)) with { JournalPath = journalPath };
            }

            var result = UsbProvisioningSerializer.DeserializeResult(await File.ReadAllTextAsync(resultPath));
            if (result?.Success != true)
            {
                RecordDiagnostic(journalPath, result?.Diagnostic);
                var failure = BuildProvisioningFailure(result, displayPath, transaction) with { JournalPath = journalPath };
                return s_cancelRequested ? AsCancelled(failure) : failure;
            }

            progress.Report(new CreatorActionService.CreatorStepProgress(CreatorStep.Complete, CreatorActionService.CreatorStepStatus.Success));
            return new CreatorActionService.CreateUsbResult(
                true,
                result.DataVolumeRoot ?? displayPath,
                new CreatorTransaction(CreatorStep.Complete),
                HostCachedSystemImagePath: systemImagePath,
                UsbSystemImagePath: result.SystemImagePath,
                DataImagePath: result.DataImagePath,
                SystemImageSha256: result.VerifiedSystemImageSha256);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return Failure("Administrator confirmation was cancelled.", displayPath, "launcher.create.error.provisioning");
        }
        catch (Exception exception)
        {
            // This covers everything from starting the provisioner onwards, so the disk
            // may already have been touched. Unknown is the honest answer.
            RecordDiagnostic(journalPath, $"launcher: {exception.GetType().FullName}: {exception.Message}");
            return Failure(
                exception.Message,
                displayPath,
                "launcher.create.error.provisioning",
                "launcher.create.error.safety.unknown") with { JournalPath = journalPath };
        }
        finally
        {
            s_activeCancelPath = null;
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, true);
                }
            }
            catch
            {
                // A later cleanup pass may remove the non-secret request/result files.
            }
        }

        CreatorActionService.CreateUsbResult Failure(
            string message,
            string path,
            string errorMessageKey = "launcher.create.error.provisioning",
            string errorSafetyMessageKey = "launcher.create.error.safety.unchanged") => new(
            false,
            path,
            transaction,
            message,
            CreatorStep.PrepareRelease,
            errorMessageKey,
            new Dictionary<string, string> { ["error"] = message },
            ErrorSafetyMessageKey: errorSafetyMessageKey);
    }

    private static volatile string? s_activeCancelPath;
    private static volatile bool s_cancelRequested;

    /// <summary>
    /// Asks the running provisioner to stop; false when no creation is running.
    /// </summary>
    /// <remarks>
    /// The provisioner runs elevated, so the unelevated launcher cannot signal it; it
    /// watches for this file instead and stops at its next cancellation point, releasing
    /// the volume locks, and the stick is left to be created again.
    /// </remarks>
    internal static bool RequestCancel()
    {
        var path = s_activeCancelPath;
        if (path is null) return false;
        try
        {
            File.WriteAllText(path, string.Empty);
            s_cancelRequested = true;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A failure that followed the operator's own request to stop, said as such.</summary>
    internal static CreatorActionService.CreateUsbResult AsCancelled(CreatorActionService.CreateUsbResult failure) => failure with
    {
        ErrorMessageKey = "launcher.create.error.cancelled",
        // Stopped before anything was written, the stick is as it was; otherwise it holds
        // part of an image and has to be created again.
        ErrorSafetyMessageKey = failure.ErrorSafetyMessageKey == "launcher.create.error.safety.unchanged"
            ? failure.ErrorSafetyMessageKey
            : "launcher.create.error.safety.possibly_modified"
    };

    /// <summary>Where USB creation journals are kept for this user.</summary>
    internal static string JournalDirectory => DexTorStorage.Journal;

    /// <summary>
    /// Adds the provisioner's own account of a failure to the journal, so the details
    /// survive even when the elevated process could not write there itself.
    /// </summary>
    internal static void RecordDiagnostic(string journalPath, string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
            File.AppendAllText(journalPath, $"{DateTimeOffset.UtcNow:O} result: {diagnostic.ReplaceLineEndings(" ")}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The message on screen still carries the failure.
        }
    }

    internal static string ResolveSystemImagePath(CreatorActionService.ReleaseConfig release)
    {
        ArgumentNullException.ThrowIfNull(release);
        return File.Exists(release.Source) ? release.Source : release.DestinationPath;
    }

    internal static CreatorActionService.CreateUsbResult BuildProvisioningFailure(
        UsbProvisioningResult? result,
        string displayPath,
        CreatorTransaction transaction)
    {
        // "Nothing was written" is only said where the provisioner established it. A
        // result that could not be read at all establishes nothing.
        var safetyKey = result?.Status switch
        {
            UsbProvisioningStatus.InterruptedAfterDiskChange => "launcher.create.error.safety.possibly_modified",
            UsbProvisioningStatus.Rejected or UsbProvisioningStatus.FailedBeforeDiskChange
                => "launcher.create.error.safety.unchanged",
            _ => "launcher.create.error.safety.unknown"
        };
        var message = result?.Message ?? "DexTor Provisioner failed.";

        var errorKey = ErrorMessageKeyFor(result?.Failure);
        var arguments = new Dictionary<string, string> { ["error"] = message };
        if (result?.Failure == UsbProvisioningFailure.DeviceFaulty && result.FailureOffsetBytes is { } offset)
        {
            // Where the stick stopped: the same place on a second attempt is the clearest
            // sign of a faulty stick or a fake capacity.
            errorKey = "launcher.create.error.device.faulty_at";
            arguments["at"] = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0} GB", offset / (1024d * 1024d * 1024d));
        }

        return new CreatorActionService.CreateUsbResult(
            false,
            displayPath,
            transaction,
            message,
            CreatorStep.FormatUsb,
            errorKey,
            arguments,
            ErrorSafetyMessageKey: safetyKey);
    }

    /// <summary>
    /// The message shown for a failure, chosen by what kind of fault it was.
    /// </summary>
    /// <remarks>
    /// Each kind gets its own sentence because each one has a different next step: an
    /// image is downloaded again, a vanished stick is moved to another port, a
    /// write-protected one has a switch, a busy one needs something closed. A single
    /// "USB preparation error" with the raw Windows text behind it gave the operator
    /// none of that.
    /// </remarks>
    internal static string ErrorMessageKeyFor(UsbProvisioningFailure? failure) => failure switch
    {
        UsbProvisioningFailure.LiveImageBootAssets => "launcher.create.error.image",
        UsbProvisioningFailure.DeviceDisappeared => "launcher.create.error.device.disappeared",
        UsbProvisioningFailure.DeviceReplaced => "launcher.create.error.device.replaced",
        UsbProvisioningFailure.DeviceNotReady => "launcher.create.error.device.not_ready",
        UsbProvisioningFailure.DeviceWriteProtected => "launcher.create.error.device.write_protected",
        UsbProvisioningFailure.DeviceBusy => "launcher.create.error.device.busy",
        UsbProvisioningFailure.DeviceFaulty => "launcher.create.error.device.faulty",
        _ => "launcher.create.error.provisioning"
    };

    /// <summary>
    /// What can be said about the device after the provisioner vanished without a result.
    /// </summary>
    internal static string SafetyKeyForLostOutcome(UsbProvisioningStage? reachedStage) =>
        reachedStage is null or UsbProvisioningStage.Validating
            ? "launcher.create.error.safety.unknown"
            : "launcher.create.error.safety.possibly_modified";

    internal static ProcessStartInfo BuildStartInfo(
        string requestPath,
        string resultPath,
        string? progressPath = null,
        string? launcherExecutable = null,
        string? journalDirectory = null,
        string? cancelPath = null)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "DexTor.Provisioner.exe");
        var assembly = Path.Combine(AppContext.BaseDirectory, "DexTor.Provisioner.dll");
        var progressArgument = string.IsNullOrWhiteSpace(progressPath) ? string.Empty : $" --progress \"{progressPath}\"";
        var journalArgument = string.IsNullOrWhiteSpace(journalDirectory) ? string.Empty : $" --journal \"{journalDirectory}\"";
        var cancelArgument = string.IsNullOrWhiteSpace(cancelPath) ? string.Empty : $" --cancel \"{cancelPath}\"";
        var arguments = $"--request \"{requestPath}\" --result \"{resultPath}\"{progressArgument}{journalArgument}{cancelArgument} --execute";
        // The launcher carries the provisioner inside itself, so the elevated process is the
        // same portable file: nothing is unpacked beside it, and the Windows prompt names
        // the program the operator actually started.
        launcherExecutable ??= ResolveRunningLauncher();
        if (!string.IsNullOrWhiteSpace(launcherExecutable))
        {
            return new ProcessStartInfo(launcherExecutable)
            {
                Arguments = $"{DexTor.Provisioner.ProvisionerCommand.ModeArgument} {arguments}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
        }

        return File.Exists(executable)
            ? new ProcessStartInfo(executable)
            {
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }
            : new ProcessStartInfo("dotnet")
            {
                Arguments = $"\"{assembly}\" {arguments}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            };
    }

    /// <summary>The launcher executable of this process, or null when hosted elsewhere (tests).</summary>
    private static string? ResolveRunningLauncher()
    {
        var entry = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        var path = Environment.ProcessPath;
        return string.Equals(entry, "DexTor.Launcher", StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Follows the provisioner's progress file, and reports the furthest stage it saw.
    /// </summary>
    private static async Task<UsbProvisioningStage?> MonitorProgressAsync(
        Process process,
        string progressPath,
        Guid requestId,
        IProgress<CreatorActionService.CreatorStepProgress> progress)
    {
        var waitForExit = process.WaitForExitAsync();
        var cursor = new ProgressCursor();
        while (!waitForExit.IsCompleted)
        {
            ReportProgressFile(progressPath, requestId, cursor, progress);
            await Task.WhenAny(waitForExit, Task.Delay(200));
        }

        await waitForExit;
        ReportProgressFile(progressPath, requestId, cursor, progress);
        return cursor.Stage;
    }

    /// <summary>What was last passed on from the progress file.</summary>
    internal sealed class ProgressCursor
    {
        internal UsbProvisioningStage? Stage { get; set; }
        internal DateTimeOffset Updated { get; set; }
    }

    internal static void ReportProgressFile(
        string path,
        Guid requestId,
        ProgressCursor cursor,
        IProgress<CreatorActionService.CreatorStepProgress> progress)
    {
        try
        {
            if (!File.Exists(path)) return;
            var update = UsbProvisioningSerializer.DeserializeProgress(File.ReadAllText(path));
            if (update is null || update.RequestId != requestId) return;
            // A new stage is always passed on; within a stage, only fresh byte counts are.
            if (update.Stage == cursor.Stage && (update.BytesTotal <= 0 || update.UpdatedUtc == cursor.Updated)) return;
            cursor.Stage = update.Stage;
            cursor.Updated = update.UpdatedUtc;
            progress.Report(new CreatorActionService.CreatorStepProgress(
                MapStage(update.Stage),
                update.Stage == UsbProvisioningStage.Complete
                    ? CreatorActionService.CreatorStepStatus.Success
                    : CreatorActionService.CreatorStepStatus.Info,
                StageMessageKey(update.Stage),
                BytesDone: update.BytesDone,
                BytesTotal: update.BytesTotal));
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            // Read mid-replace: the next poll sees the file whole.
        }
    }

    internal static CreatorStep MapStage(UsbProvisioningStage stage) => stage switch
    {
        UsbProvisioningStage.Validating => CreatorStep.ConfirmErase,
        UsbProvisioningStage.CleaningDisk
            or UsbProvisioningStage.FormattingDataVolume
            or UsbProvisioningStage.WaitingForDataVolume
            // Waiting for the device to come back belongs to the step that was running,
            // not to "finished", which is where the default would have put it.
            or UsbProvisioningStage.WaitingForDevice => CreatorStep.FormatUsb,
        UsbProvisioningStage.WritingSystemImage or UsbProvisioningStage.CopyingWindowsImage => CreatorStep.WriteSystemImage,
        UsbProvisioningStage.CreatingPrivateStorage => CreatorStep.CreateDataImage,
        UsbProvisioningStage.Verifying => CreatorStep.VerifyUsb,
        _ => CreatorStep.Complete
    };

    internal static string StageMessageKey(UsbProvisioningStage stage) => $"launcher.wizard.create.provisioner_{stage.ToString().ToLowerInvariant()}";
}
