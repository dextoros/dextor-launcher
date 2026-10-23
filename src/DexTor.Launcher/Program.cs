using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;
using System.Security.Principal;
using System.IO;
using System.Globalization;

using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.SessionHost;
using DexTor.Platform.Windows;

namespace DexTor.Launcher;

public static class Program
{
    private static readonly SessionHostService SessionHostService = new();

    private static Func<string, ILocalizer, MessageBoxButtons, DialogResult> ShowConfirmationCallback = ShowConfirmation;
    private static Func<string, ILocalizer, MessageBoxIcon, int> ShowActionMessageCallback = ShowActionMessage;
    private static Func<bool> TryRestartWindowsCallback = TryRestartWindows;
    private static Action<string> OpenUrlCallback = OpenUrl;
    private static Func<bool> IsCurrentUserAdminCallback = IsCurrentUserAdmin;
    private static Func<bool> TryEnableVirtualizationCallback = TryEnableVirtualization;
    private static Func<bool> TryEnableVirtualizationElevatedCallback = ElevatedWhpxEnabler.TryRunElevated;
    private static Func<bool> IsPackagedCallback = static () => PackagedApp.IsPackaged;
    private static Func<string, ILocalizer, MessageBoxButtons, DialogResult> ConfirmFormatImageCallback = ShowConfirmation;
    private static Func<string, ILocalizer, string?, bool> ConfirmEraseCallback = ShowEraseConfirmation;

    [STAThread]
    private static int Main(string[] args)
    {
        // The portable launcher is one file. For USB creation it restarts itself elevated
        // in this mode; no window, mutex or wizard state exists yet, and none is created.
        if (args.Length > 0 && string.Equals(args[0], DexTor.Provisioner.ProvisionerCommand.ModeArgument, StringComparison.OrdinalIgnoreCase))
        {
            return DexTor.Provisioner.ProvisionerCommand.RunAsync(args[1..]).GetAwaiter().GetResult();
        }

        // The Store launcher's elevated copy that only turns on Windows Hypervisor Platform.
        if (args.Length > 0 && string.Equals(args[0], ElevatedWhpxEnabler.ModeArgument, StringComparison.OrdinalIgnoreCase))
        {
            return TryEnableVirtualization() ? 0 : 1;
        }

        // The language the operator picked last time, else Windows' own.
        var localizer = new Localizer(LauncherRuntimeState.ResolveStartLanguage());

        // Drawn at the display's own scale: the wizard sizes itself in design pixels times the
        // system DPI (WizardDesign.Scale). Unaware, Windows stretched a 100 % bitmap and every
        // text came out soft at 125 %.
        System.Windows.Forms.Application.SetHighDpiMode(HighDpiMode.SystemAware);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var singleInstance = TryAcquireSingleInstance("Local\\DexTor.OS.Windows.Launcher", out var isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show(
                localizer.T("launcher.single_instance.message"),
                localizer.T("launcher.wizard.window_title"),
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }
        var dextorCacheRoot = DexTorStorage.Root;
        // Caches moved from %TEMP% to %LOCALAPPDATA%; a verified image is carried over
        // rather than downloaded again, and the old folder is cleared.
        TemporaryCacheCleaner.MigrateFromTemp(DexTorStorage.LegacyTempRoot, dextorCacheRoot);
        TemporaryCacheCleaner.MigrateImageFolder(dextorCacheRoot);
        // Only one launcher runs, so nothing of ours is downloading or unpacking now:
        // partial files a killed run left behind are garbage and are removed first.
        TemporaryCacheCleaner.Clean(dextorCacheRoot, DateTime.UtcNow);
        // Shortcuts and uninstall entries an earlier QEMU unpack left on this PC.
        QemuInstallerTraces.Remove(DexTorStorage.Runtime);
        QemuInstallerTraces.Remove(Path.Combine(DexTorStorage.LegacyTempRoot, "Runtime"));
        var cacheRoot = Path.Combine(dextorCacheRoot, DexTorStorage.LiveImageFolder);
        var runtimeCacheRoot = Path.Combine(dextorCacheRoot, "Runtime");
        var imageService = new LiveImagePreparationService();
        var runtimeService = new QemuRuntimePreparationService();
        var preparationService = new PreparationBundleService(
            imageService,
            runtimeService,
            cacheRoot,
            runtimeCacheRoot);
        // The wizard, once made: the VM opens on the monitor it is on (VmDisplayPlanner).
        var wizardHandle = IntPtr.Zero;
        using var curtain = new BootCurtain();
        SessionHostService.BootCurtain = curtain;
        var dependencies = new LauncherWizardDependencies
        {
            // The evaluator does its network and virtualization probes synchronously, for
            // up to several seconds. On the UI thread that froze a half-built page on
            // screen, so it runs on the pool and the wizard keeps painting.
            RefreshEnvironment = () => Task.Run(async () =>
            {
                var report = await new HostPreflightEvaluator().EvaluateAsync();
                return new LauncherWizardEnvironment(report, ResolveLauncherContext(report));
            }),
            PreparePc = () => RunPrepareFlowInlineAsync(localizer),
            RestartWindows = TryRestartWindows,
            InspectImage = preparationService.InspectAsync,
            PrepareImage = preparationService.PrepareAsync,
            InspectUsbDrives = UsbDiscoveryService.InspectPhysicalUsbDrivesAsync,
            MeasureUsbSpeed = UsbDiscoveryService.MeasureAccessSpeedCachedAsync,
            PrepareHostForCreation = usb =>
            {
                ExplorerWindows.CloseOn(usb.RootPath);
                return AutoPlaySuppressor.TryStart();
            },
            HasEnoughSpace = (path, imageSize) => UsbDiscoveryService.HasEnoughSpace(
                path,
                imageSize,
                UsbDiscoveryService.RecommendedUsbReservedBytes,
                out _,
                out _),
            ConfirmErase = usb => ConfirmEraseCallback(
                localizer.T("launcher.wizard.usb.confirm_erase", new Dictionary<string, string>
                {
                    ["drive"] = string.IsNullOrWhiteSpace(usb.RootPath)
                        ? $"PhysicalDrive{usb.PhysicalIdentity?.DiskNumber}"
                        : usb.RootPath,
                    ["disk"] = usb.PhysicalIdentity?.DiskNumber.ToString(CultureInfo.InvariantCulture) ?? "—",
                    ["model"] = usb.PhysicalIdentity?.FriendlyName ?? "—",
                    ["capacity"] = usb.TotalSizeBytes is { } bytes
                        ? LauncherWizardWindow.FormatSize(bytes, localizer)
                        : "—"
                }),
                localizer,
                // An external hard disk is erased only after an explicit "I understand".
                usb.IsExternalDisk ? localizer.T("launcher.wizard.usb.acknowledge") : null),
            CreateUsb = RunWizardCreateAsync,
            CancelCreation = UsbProvisionerClient.RequestCancel,
            VerifyDexTorUsb = (path, progress, token) => DexTorUsbReadinessVerifier.VerifyAsync(
                path, progress, verifySystemImageHash: false, token),
            StartDexTor = async (path, progress) =>
            {
                var runtimeProgress = new Progress<LiveImagePreparationProgress>(update => progress.Report(
                    new SessionStartProgress(
                        SessionStartStage.CheckingRuntime,
                        update.BytesDownloaded,
                        update.TotalBytes,
                        update.BytesPerSecond,
                        update.EstimatedRemaining)));
                var runtime = await runtimeService.PrepareAsync(runtimeCacheRoot, runtimeProgress, CancellationToken.None);
                if (!runtime.IsReady)
                {
                    throw new InvalidOperationException(runtime.ErrorMessage ?? "Could not prepare the Windows virtualization runtime.");
                }
                _ = localizer.TryGetLanguage(out var guestLanguage);
                SessionHostService.GuestLanguage = guestLanguage;
                SessionHostService.Display = wizardHandle != IntPtr.Zero ? VmDisplayPlanner.ForWindow(wizardHandle) : null;
                await SessionHostService.StartAsync(path, progress);
            },
            StopDexTor = progress => SessionHostService.StopAsync(progress),
            EjectDexTor = (path, progress) => SessionHostService.EjectAsync(path, progress),
            EmergencyStopDexTor = () => SessionHostService.EmergencyStopAsync(),
            IsActiveUsbAvailable = IsActiveDexTorUsbAvailable,
            ShowEmergencyNotification = ShowEmergencyNotificationAsync,
            GetSessionState = () => SessionHostService.State,
            GetSessionError = () => SessionHostService.LastError,
            EndedByGuestShutdown = () => SessionHostService.EndedByGuestShutdown,
            StorageShortfall = StorageSpace.Shortfall,
            IsVmPausedForSpace = () => SessionHostService.IsPausedForSpace,
            ResumeDexTor = () => SessionHostService.ResumeAfterSpaceFreedAsync(),
            ShowVmWindowNow = SessionHostService.ShowWindowNow,
            SaveLanguage = LauncherRuntimeState.SaveLanguage
        };

        using var window = new LauncherWizardWindow(localizer, dependencies);
        window.HandleCreated += (_, _) => wizardHandle = window.Handle;
        System.Windows.Forms.Application.Run(window);
        return 0;
    }

    internal static Mutex TryAcquireSingleInstance(string name, out bool isFirstInstance) =>
        new(false, name, out isFirstInstance);

    internal static bool IsActiveDexTorUsbAvailable(string rootPath)
    {
        try
        {
            var root = Path.GetFullPath(rootPath);
            return Directory.Exists(root)
                && File.Exists(Path.Combine(root, "DEXTOR", "metadata", "device.json"))
                && File.Exists(Path.Combine(root, "DEXTOR", "storage", "data.img"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static async Task ShowEmergencyNotificationAsync(string title, string message)
    {
        using var notification = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Warning,
            Text = title.Length <= 63 ? title : title[..63],
            BalloonTipTitle = title,
            BalloonTipText = message,
            BalloonTipIcon = ToolTipIcon.Warning,
            Visible = true
        };
        notification.ShowBalloonTip(4000);
        await Task.Delay(2500);
    }

    private static async Task<CreatorActionService.CreateUsbResult> RunWizardCreateAsync(
        UsbDriveSnapshot usb,
        IProgress<CreatorActionService.CreatorStepProgress> progress)
    {
        var usbRootPath = !string.IsNullOrWhiteSpace(usb.RootPath)
            ? usb.RootPath
            : usb.PhysicalIdentity is { } identity
                ? $"PhysicalDrive{identity.DiskNumber}"
                : string.Empty;
        var transaction = CreatorActionService.TryLoadTransaction(usbRootPath) ?? CreatorActionService.CreateTransaction();
        if (CreatorActionService.TryGetReleaseConfigFromEnvironment(out var releaseInfo, out var errorMessageKey)
            && releaseInfo is not null)
        {
            if (usb.PhysicalIdentity is not null)
            {
                return await UsbProvisionerClient.CreateAsync(
                    usb,
                    UsbProvisionerClient.ResolveSystemImagePath(releaseInfo),
                    releaseInfo.Sha256,
                    progress);
            }

            return await CreatorActionService.RunCreateFlowWithReleaseAsync(
                usbRootPath,
                releaseInfo.Source,
                releaseInfo.Sha256,
                releaseInfo.DestinationPath,
                progress,
                failStep: null,
                releaseManifestPath: releaseInfo.ManifestPath,
                transaction: transaction);
        }

        if (!string.IsNullOrWhiteSpace(errorMessageKey))
        {
            return new CreatorActionService.CreateUsbResult(
                false,
                usbRootPath,
                transaction,
                errorMessageKey,
                CreatorStep.PrepareRelease,
                errorMessageKey,
                null,
                CreatorActionService.GetReleaseConfigRecoveryPath());
        }

        return await Task.Run(() => CreatorActionService.RunCreateFlow(
            usbRootPath,
            progress,
            failStep: null,
            stepDelayMs: 130,
            transaction: transaction));
    }

    private static LauncherContext ResolveLauncherContext(PreflightReport preflight)
    {
        var hasPrepareRebootHint = LauncherRuntimeState.HasPrepareRebootHint();
        if (preflight.IsWhpxReady && hasPrepareRebootHint)
        {
            LauncherRuntimeState.ClearPrepareRebootHint();
            hasPrepareRebootHint = false;
        }

        var sessionFlags = ResolveLauncherSessionFlags(SessionHostService.State);
        var removableRoot = UsbDiscoveryService.DetectRemovableRootDrives();
        var hasRemovableUsb = removableRoot.Length > 0;
        var dextorRootPath = UsbDiscoveryService.DetectDextorUsb(removableRoot);
        var hasDextorUsb = dextorRootPath is not null;
        var createRootCandidates = UsbDiscoveryService.BuildCreateCandidates(removableRoot, dextorRootPath);
        var createRootPath = createRootCandidates.Length == 1 ? createRootCandidates[0] : null;
        var hasSetupInterrupted = dextorRootPath is not null && CreatorActionService.HasSetupInterrupted(dextorRootPath);
        var needsReboot = hasPrepareRebootHint && !preflight.IsWhpxReady;

        return new LauncherContext
        {
            IsUsbConnected = hasRemovableUsb,
            IsDexTorUsb = hasDextorUsb,
            HasSetupInterrupted = hasSetupInterrupted,
            IsWhpxEnabled = preflight.IsWhpxReady,
            HasRequiredUpdate = false,
            HasOptionalUpdate = false,
            NeedsReboot = needsReboot,
            UsbRootPath = createRootPath,
            UsbRootCandidates = createRootCandidates,
            DextorRootPath = dextorRootPath,
            IsSessionRunning = sessionFlags.IsSessionRunning,
            HasUncleanSession = sessionFlags.HasUncleanSession
        };
    }

    private static (bool IsSessionRunning, bool HasUncleanSession) ResolveLauncherSessionFlags(SessionState state)
    {
        return state switch
        {
            SessionState.Running or SessionState.StartingQemu or SessionState.StorageLocking => (true, state != SessionState.Running),
            SessionState.Stopping => (false, true),
            SessionState.SafeToEject or SessionState.Idle => (false, false),
            _ => (false, false)
        };
    }

    /// <summary>
    /// Runs one primary action. Asynchronous because starting and stopping a session are
    /// genuinely asynchronous, and blocking the UI thread on them deadlocks against the
    /// message pump the session lifecycle itself needs.
    /// </summary>
    private static Task<int> ExecutePrimaryActionAsync(
        LauncherPrimaryAction action,
        PreflightReport preflight,
        LauncherContext context,
        ILocalizer localizer)
    {
        return action switch
        {
            LauncherPrimaryAction.StartDexTor => HandleStartDextorAsync(context, localizer),
            LauncherPrimaryAction.EndSession => HandleStopSessionAsync(localizer),
            LauncherPrimaryAction.EjectUsb => HandleEjectUsbAsync(context, localizer),
            _ => Task.FromResult(action switch
            {
                LauncherPrimaryAction.ConnectDexTorUsb => HandleConnectUsb(context, localizer),
                LauncherPrimaryAction.CreateDexTorUsb => HandleCreateUsb(context, localizer),
                LauncherPrimaryAction.ContinueSetup => HandleCreateSetup(context, localizer, false),
                LauncherPrimaryAction.StartOverSetup => HandleCreateSetup(context, localizer, true),
                LauncherPrimaryAction.UpdateAndStart => HandleUnavailable(localizer),
                LauncherPrimaryAction.Prepare => HandlePrepare(preflight, localizer),
                LauncherPrimaryAction.RestartRequired => HandleRestartRequired(localizer),
                LauncherPrimaryAction.CheckAndRecover => HandleCheckAndRecover(context, localizer),
                LauncherPrimaryAction.RepairAndStart => HandleRepairAndStart(context, localizer),
                LauncherPrimaryAction.OpenDexTor => HandleOpenDexTor(context, localizer),
                LauncherPrimaryAction.Continue => HandleImagePreparationStep(preflight, localizer),
                _ => 0
            })
        };
    }

    private static int HandleImagePreparationStep(PreflightReport preflight, ILocalizer localizer)
    {
        var cacheRoot = DexTorStorage.LiveImage;
        var service = new LiveImagePreparationService();
        var preparedImage = LiveImageSetupWindow.Run(
            localizer,
            (token, progress) => service.InspectCacheFromIndexAsync(cacheRoot, progress, token),
            (token, progress) => service.PrepareFromIndexAsync(cacheRoot, progress, token),
            HasWritableCreationUsb);

        if (preparedImage is null)
        {
            return 0;
        }

        return HandleCreateUsbWithPreparedImage(ResolveLauncherContext(preflight), localizer, preparedImage);
    }

    private static bool HasWritableCreationUsb()
    {
        var removableRoots = UsbDiscoveryService.DetectRemovableRootDrives();
        var dextorRoot = UsbDiscoveryService.DetectDextorUsb(removableRoots);
        return UsbDiscoveryService.BuildCreateCandidates(removableRoots, dextorRoot).Length > 0;
    }

    private static int HandleConnectUsb(LauncherContext context, ILocalizer localizer)
    {
        if (context.IsUsbConnected)
        {
            return 0;
        }

        return ShowActionMessage(localizer.T("launcher.action.connect.prompt"), localizer);
    }

    private static int HandleCreateUsb(LauncherContext context, ILocalizer localizer)
    {
        return HandleCreateUsbWithPreparedImage(context, localizer, null);
    }

    private static int HandleCreateUsbWithPreparedImage(
        LauncherContext context,
        ILocalizer localizer,
        LiveImagePreparationResult? preparedImage)
    {
        if (context.IsDexTorUsb)
        {
            return ShowActionMessageFromKey("launcher.action.unavailable", localizer);
        }

        if (context.UsbRootCandidates.Count == 0)
        {
            if (context.IsUsbConnected)
            {
                return ShowActionMessageCallback(
                    BuildActionMessage(
                        localizer,
                        "launcher.action.create.no_writable_usb",
                        "launcher.action.create.no_writable_usb.solution"),
                    localizer,
                    MessageBoxIcon.Warning);
            }

            return ShowActionMessageCallback(localizer.T("launcher.action.connect.prompt"), localizer, MessageBoxIcon.Information);
        }

        var usbToCreate = ResolveUsbForCreation(context, localizer);
        if (usbToCreate is null)
        {
            return ShowActionMessageFromKey("launcher.action.create.cancelled", localizer);
        }

        var prepareResult = preparedImage ?? PrepareLiveImageForCreation(localizer);
        if (!prepareResult.Success)
        {
            return ShowActionMessage(
                prepareResult.ErrorMessage ?? localizer.T("launcher.create.live_image.prepare.failed"),
                localizer,
                MessageBoxIcon.Error);
        }

        if (string.IsNullOrWhiteSpace(prepareResult.LocalImagePath) || prepareResult.SourceSizeBytes <= 0)
        {
            return ShowActionMessageFromKey("launcher.create.live_image.prepare.no_source", localizer);
        }

        var requiredBytes = prepareResult.SourceSizeBytes + UsbDiscoveryService.RecommendedUsbReservedBytes;
        var requiredGb = ToRoundedGigabytes(requiredBytes);
        if (!UsbDiscoveryService.HasEnoughSpace(
                usbToCreate!,
                prepareResult.SourceSizeBytes,
                UsbDiscoveryService.RecommendedUsbReservedBytes,
                out _,
                out _))
        {
            return ShowActionMessage(
                BuildActionMessageWithArgs(
                    localizer,
                    "launcher.create.live_image.prepare.no_space",
                    "launcher.create.live_image.prepare.confirm_format_solution",
                    messageArgs: new Dictionary<string, string>
                    {
                        ["required_gb"] = requiredGb
                    }),
                localizer,
                MessageBoxIcon.Warning);
        }

        var formatConfirm = localizer.T(
            "launcher.create.live_image.prepare.confirm_format",
            new Dictionary<string, string>
            {
                ["image_size"] = ToHumanBytes(prepareResult.SourceSizeBytes),
                ["required_gb"] = requiredGb
            });
        if (ConfirmFormatImageCallback(
                formatConfirm,
                localizer,
                MessageBoxButtons.YesNo) != DialogResult.Yes)
        {
            return ShowActionMessageFromKey("launcher.action.create.cancelled", localizer);
        }

        var cachePath = DexTorStorage.Release;
        Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSourceEnvVar, prepareResult.LocalImagePath);
        Environment.SetEnvironmentVariable(CreatorActionService.ReleaseSha256EnvVar, prepareResult.SourceSha256);
        Environment.SetEnvironmentVariable(CreatorActionService.ReleaseCacheEnvVar, cachePath);

        var result = CreateUsbProgressWindow.Run(usbToCreate!, localizer);
        if (result.Success)
        {
            return ShowActionMessageCallback(
                localizer.T(
                    "launcher.action.create.success",
                    new Dictionary<string, string>
                    {
                        ["drive"] = result.UsbRootPath
                    }),
                localizer,
                MessageBoxIcon.Information);
        }

        var errorMessage = result.ErrorMessageKey is null
            ? (result.ErrorMessage ?? localizer.T("launcher.action.create.error.unknown"))
            : localizer.T(result.ErrorMessageKey, result.ErrorMessageArgs);
        var solutionKey = CreatorActionService.GetCreateErrorSolutionKey(result.ErrorMessageKey);
        var message = BuildCreateErrorMessage(
            localizer,
            usbToCreate!,
            result.ErrorMessageKey ?? "launcher.create.error.unknown",
            errorMessage,
            result.ErrorSafetyMessageKey);

        return ShowActionMessageCallback(message, localizer, MessageBoxIcon.Error);
    }

    private static int HandleCreateSetup(LauncherContext context, ILocalizer localizer, bool startOver)
    {
        if (!context.HasSetupInterrupted || context.DextorRootPath is null || !context.IsDexTorUsb)
        {
            return ShowActionMessage(localizer.T("launcher.action.create.unavailable"), localizer, MessageBoxIcon.Error);
        }

        if (startOver)
        {
            CreatorActionService.DeleteTransactionState(context.DextorRootPath);
        }

        var result = CreateUsbProgressWindow.Run(context.DextorRootPath, localizer, continueExisting: !startOver);
        if (result.Success)
        {
            return ShowActionMessage(
                localizer.T(
                    "launcher.action.create.success",
                    new Dictionary<string, string>
                    {
                        ["drive"] = result.UsbRootPath
                    }),
                localizer);
        }

        var errorMessage = result.ErrorMessageKey is null
            ? (result.ErrorMessage ?? localizer.T("launcher.action.create.error.unknown"))
            : localizer.T(result.ErrorMessageKey, result.ErrorMessageArgs);
        var solutionKey = CreatorActionService.GetCreateErrorSolutionKey(result.ErrorMessageKey);
        var message = BuildCreateErrorMessage(
            localizer,
            context.DextorRootPath,
            result.ErrorMessageKey ?? "launcher.create.error.unknown",
            errorMessage,
            result.ErrorSafetyMessageKey);

        return ShowActionMessage(message, localizer, MessageBoxIcon.Error);
    }

    private static string BuildCreateErrorMessage(
        ILocalizer localizer,
        string drive,
        string errorMessageKey,
        string errorMessage,
        string? errorSafetyMessageKey)
    {
        var message = localizer.T(
            "launcher.action.create.error",
            new Dictionary<string, string>
            {
                ["drive"] = drive,
                ["error"] = errorMessage
            });

        var solutionKey = CreatorActionService.GetCreateErrorSolutionKey(errorMessageKey);
        var parts = new List<string>
        {
            $"{localizer.T("launcher.solution.label")} {localizer.T(solutionKey)}"
        };

        if (!string.IsNullOrWhiteSpace(errorSafetyMessageKey))
        {
            parts.Add($"{localizer.T("launcher.create.error.safety.label")} {localizer.T(errorSafetyMessageKey)}");
        }

        return message + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine, parts);
    }

    private static LiveImagePreparationResult PrepareLiveImageForCreation(ILocalizer localizer)
    {
        var confirm = localizer.T(
            "launcher.create.live_image.prepare.confirm",
            new Dictionary<string, string>
            {
                ["url"] = LiveImagePreparationService.LiveImageSourceIndexUrl
            });
        if (ShowConfirmationCallback(confirm, localizer, MessageBoxButtons.YesNo) != DialogResult.Yes)
        {
            return new LiveImagePreparationResult(
                false,
                null,
                null,
                null,
                0,
                localizer.T("launcher.action.create.cancelled"),
                "launcher.action.create.cancelled");
        }

        var cacheRoot = DexTorStorage.LiveImage;
        var service = new LiveImagePreparationService();
        var title = localizer.T("launcher.create.live_image.prepare.title");
        var subtitle = localizer.T(
            "launcher.create.live_image.prepare.subtitle",
            new Dictionary<string, string>
            {
                ["source"] = LiveImagePreparationService.LiveImageSourceIndexUrl
            });

        return LiveImagePrepareWindow.Run(localizer, (token, progress) => service.PrepareFromIndexAsync(cacheRoot, progress, token), title, subtitle);
    }

    private static string ToRoundedGigabytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0";
        }

        var value = bytes / (1024d * 1024d * 1024d);
        return Math.Ceiling(value).ToString(CultureInfo.InvariantCulture);
    }

    private static string ToHumanBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var units = new[] { "B", "KB", "MB", "GB", "TB" };
        var value = (double)bytes;
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.00} {units[unitIndex]}";
    }

    private static async Task<int> HandleStartDextorAsync(LauncherContext context, ILocalizer localizer)
    {
        var resolved = ResolveStartActionMessage(context);
        if (resolved.MessageKey == "launcher.action.start.prompt")
        {
            _ = localizer.TryGetLanguage(out var guestLanguage);
            SessionHostService.GuestLanguage = guestLanguage;
            await SessionHostService.StartAsync(context.DextorRootPath!).ConfigureAwait(true);
        }

        return ShowActionMessageFromKey(
            resolved.MessageKey,
            localizer,
            resolved.Icon,
            resolved.SolutionKey);
    }

    private static async Task<int> HandleStopSessionAsync(ILocalizer localizer)
    {
        var report = await SessionHostService.StopAsync().ConfigureAwait(true);
        return ShowActionMessage(
            localizer.T("launcher.action.end.prompt") + DescribeFinalization(report, localizer),
            localizer,
            report.WriterTermination == WriterTerminationResult.Confirmed
                ? MessageBoxIcon.Information
                : MessageBoxIcon.Warning);
    }

    private static async Task<int> HandleEjectUsbAsync(LauncherContext context, ILocalizer localizer)
    {
        var resolved = ResolveEjectMessage(context);
        if (resolved.MessageKey != "launcher.action.eject.prompt" || context.DextorRootPath is null)
        {
            var payload = BuildEjectActionResult(context, localizer);
            return ShowActionMessage(payload.Message, localizer, payload.Icon);
        }

        // A standalone eject re-establishes ownership and revalidates the device itself;
        // an earlier stop result says nothing about the device now.
        var report = await SessionHostService.EjectAsync(context.DextorRootPath).ConfigureAwait(true);
        return ShowActionMessage(
            DescribeFinalization(report, localizer).TrimStart(),
            localizer,
            report.CanRemoveUsb ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Turns the independent outcomes into text, keeping every warning visible.
    /// </summary>
    /// <remarks>
    /// Removal success does not erase a forced stop or unverified storage, and unverified
    /// storage is never phrased as damage. The sentences are separate because the facts
    /// are separate.
    /// </remarks>
    internal static string DescribeFinalization(SessionFinalizationReport report, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);

        var lines = new List<string>();
        if (report.WriterTermination != WriterTerminationResult.Confirmed)
        {
            lines.Add(localizer.T("launcher.session.termination_unconfirmed"));
            return lines.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, lines);
        }

        if (report.WasForced)
        {
            lines.Add(localizer.T("launcher.session.forced_stop"));
        }

        switch (report.Flush)
        {
            case HostFlushResult.Denied:
                lines.Add(localizer.T("launcher.session.flush_denied"));
                break;
            case HostFlushResult.Failed:
            case HostFlushResult.Unavailable:
                lines.Add(localizer.T("launcher.session.flush_failed"));
                break;
            default:
                break;
        }

        if (report.GuestStorageClose != GuestStorageCloseEvidence.Confirmed)
        {
            lines.Add(localizer.T("launcher.session.storage_unverified"));
        }

        switch (report.DeviceRemoval)
        {
            case DeviceRemovalResult.Removed:
                lines.Insert(0, localizer.T("launcher.session.usb_removable"));
                break;
            case DeviceRemovalResult.Vetoed:
                lines.Add(localizer.T("launcher.session.removal_vetoed")
                    + BuildDetailSuffix(report.DeviceRemovalDetail));
                break;
            case DeviceRemovalResult.Denied:
            case DeviceRemovalResult.Failed:
            case DeviceRemovalResult.TargetUnresolved:
                lines.Add(localizer.T("launcher.session.removal_failed")
                    + BuildDetailSuffix(report.DeviceRemovalDetail));
                break;
            default:
                break;
        }

        return lines.Count == 0 ? string.Empty : Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private static string BuildDetailSuffix(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : " " + detail;

    private static int HandleCheckAndRecover(LauncherContext context, ILocalizer localizer)
    {
        var resolved = ResolveRecoverMessage(context);
        return ShowActionMessageFromKey(
            resolved.MessageKey,
            localizer,
            resolved.Icon,
            resolved.SolutionKey);
    }

    private static int HandleRepairAndStart(LauncherContext context, ILocalizer localizer)
    {
        var resolved = ResolveRepairAndStartMessage(context);
        return ShowActionMessageFromKey(
            resolved.MessageKey,
            localizer,
            resolved.Icon,
            resolved.SolutionKey);
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) ResolveRestartRequiredMessage()
    {
        return ("launcher.action.restart_required.prompt", MessageBoxIcon.Warning, "launcher.action.restart_required.solution");
    }

    private static int HandlePrepare(PreflightReport preflight, ILocalizer localizer)
    {
        if (!IsCurrentUserAdminCallback())
        {
            OpenPrepareLinks(preflight);
            return ShowActionMessageFromKey(
                "launcher.action.prepare.permission",
                localizer,
                MessageBoxIcon.Warning,
                "launcher.action.prepare.permission.solution");
        }

        if (!TryEnableVirtualizationCallback())
        {
            OpenPrepareLinks(preflight);
            return ShowActionMessageFromKey(
                "launcher.action.prepare.failed",
                localizer,
                MessageBoxIcon.Warning,
                "launcher.action.prepare.failed.solution");
        }

        LauncherRuntimeState.SetPrepareRebootHint();
        return ShowActionMessageFromKey(
            "launcher.action.prepare.success",
            localizer,
            MessageBoxIcon.Information,
            "launcher.action.prepare.success.solution");
    }

    private static async Task<LauncherInlineActionResult> RunPrepareFlowInlineAsync(Localizer localizer)
    {
        // A Store launcher cannot be started as administrator, so instead of asking for that
        // it restarts itself elevated for this one step (ElevatedWhpxEnabler).
        var elevate = !IsCurrentUserAdminCallback();
        if (elevate && !IsPackagedCallback())
        {
            return new LauncherInlineActionResult(
                false,
                localizer.T("launcher.prepare_progress.failed"),
                BuildActionMessage(
                    localizer,
                    "launcher.action.prepare.permission",
                    "launcher.action.prepare.permission.solution"));
        }

        var success = false;
        try
        {
            success = await Task.Run(elevate ? TryEnableVirtualizationElevatedCallback : TryEnableVirtualizationCallback);
            if (success)
            {
                LauncherRuntimeState.SetPrepareRebootHint();
            }
        }
        catch
        {
            success = false;
        }

        return success
            ? new LauncherInlineActionResult(
                true,
                localizer.T("launcher.prepare_progress.completed"),
                BuildActionMessage(
                    localizer,
                    "launcher.action.prepare.success",
                    "launcher.action.prepare.success.solution"))
            : new LauncherInlineActionResult(
                false,
                localizer.T("launcher.prepare_progress.failed"),
                BuildActionMessage(
                    localizer,
                    "launcher.action.prepare.failed",
                    "launcher.action.prepare.failed.solution"));
    }

    private static int HandleRestartRequired(ILocalizer localizer)
    {
        var confirm = ShowConfirmationCallback(
            localizer.T("launcher.action.restart_required.confirm"),
            localizer,
            MessageBoxButtons.YesNo);
        if (confirm == DialogResult.No)
        {
            return 0;
        }

        if (TryRestartWindowsCallback())
        {
            return 0;
        }

        return ShowActionMessageFromKey(
            "launcher.action.restart_required.failed",
            localizer,
            MessageBoxIcon.Error,
            "launcher.action.restart_required.failed.solution");
    }

    private static int ShowActionMessageFromKey(
        string messageKey,
        ILocalizer localizer,
        MessageBoxIcon icon = MessageBoxIcon.Information,
        string? solutionKey = null)
    {
        return ShowActionMessageCallback(BuildActionMessage(localizer, messageKey, solutionKey), localizer, icon);
    }

    /// <summary>
    /// The erase question, asked in the wizard's style over the wizard window.
    /// </summary>
    private static bool ShowEraseConfirmation(string message, ILocalizer localizer, string? acknowledgement) =>
        LauncherWizardWindow.AskConfirmation(
            Form.ActiveForm,
            localizer,
            message,
            localizer.T("launcher.wizard.usb.erase_action"),
            localizer.T("launcher.wizard.dialog.cancel"),
            destructive: true,
            acknowledgement: acknowledgement);

    private static DialogResult ShowConfirmation(string message, ILocalizer localizer, MessageBoxButtons buttons)
    {
        return MessageBox.Show(
            message,
            localizer.T("launcher.preflight.title"),
            buttons,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1);
    }

    private static bool TryRestartWindows()
    {
        try
        {
            var info = new ProcessStartInfo("shutdown", "/r /t 0")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(info);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsCurrentUserAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryEnableVirtualization()
    {
        try
        {
            return new WindowsHostProbe().EnableWhpxAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    private static int HandleOpenDexTor(LauncherContext context, ILocalizer localizer)
    {
        var payload = BuildOpenDexTorResult(context, localizer);
        return ShowActionMessage(payload.Message, localizer, payload.Icon);
    }

    private static int HandleEjectUsb(LauncherContext context, ILocalizer localizer)
    {
        var payload = BuildEjectActionResult(context, localizer);
        return ShowActionMessage(payload.Message, localizer, payload.Icon);
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) ResolveStartActionMessage(LauncherContext context)
    {
        if (context.HasUncleanSession)
        {
            return ("launcher.action.start.unready", MessageBoxIcon.Warning, "launcher.action.start.solution.unready");
        }

        if (context.DextorRootPath is null)
        {
            return ("launcher.action.start.no_usb", MessageBoxIcon.Information, "launcher.action.start.solution.no_usb");
        }

        return ("launcher.action.start.prompt", MessageBoxIcon.Information, null);
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) ResolveRecoverMessage(LauncherContext context)
    {
        if (!context.HasUncleanSession || context.DextorRootPath is null)
        {
            return ("launcher.action.unavailable", MessageBoxIcon.Information, "launcher.action.recover.solution.none");
        }

        return ("launcher.action.recover.prompt", MessageBoxIcon.Warning, "launcher.action.recover.solution");
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) ResolveRepairAndStartMessage(LauncherContext context)
    {
        if (!context.HasOptionalUpdate)
        {
            return ("launcher.action.unavailable", MessageBoxIcon.Information, "launcher.action.repair.solution.none");
        }

        if (context.HasUncleanSession)
        {
            return ("launcher.action.repair.unready", MessageBoxIcon.Warning, "launcher.action.repair.solution.unready");
        }

        if (context.DextorRootPath is null)
        {
            return ("launcher.action.repair.no_usb", MessageBoxIcon.Information, "launcher.action.repair.solution.no_usb");
        }

        return ("launcher.action.repair.prompt", MessageBoxIcon.Warning, "launcher.action.repair.solution");
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) ResolveOpenDexTorMessage(LauncherContext context)
    {
        if (context.DextorRootPath is not null && context.IsDexTorUsb)
        {
            return ("launcher.action.open.prompt", MessageBoxIcon.Information, "launcher.action.open.solution");
        }

        if (context.DextorRootPath is not null)
        {
            return ("launcher.action.unavailable", MessageBoxIcon.Information, "launcher.action.open.solution");
        }

        return ("launcher.action.unavailable", MessageBoxIcon.Information, "launcher.action.open.solution.no_usb");
    }

    private static (string MessageKey, MessageBoxIcon Icon, string? SolutionKey) ResolveEjectMessage(LauncherContext context)
    {
        if (context.IsSessionRunning)
        {
            return ("launcher.action.eject.running", MessageBoxIcon.Warning, "launcher.action.eject.solution.running");
        }

        if (context.HasUncleanSession)
        {
            return ("launcher.action.eject.unready", MessageBoxIcon.Warning, "launcher.action.eject.solution.unready");
        }

        if (context.DextorRootPath is null)
        {
            return ("launcher.action.eject.no_usb", MessageBoxIcon.Information, "launcher.action.eject.solution.no_usb");
        }

        return ("launcher.action.eject.prompt", MessageBoxIcon.Information, "launcher.action.eject.solution");
    }

    private static (string Message, MessageBoxIcon Icon) BuildOpenDexTorResult(LauncherContext context, ILocalizer localizer)
    {
        var resolved = ResolveOpenDexTorMessage(context);
        if (resolved.MessageKey == "launcher.action.unavailable")
        {
            return (BuildActionMessage(localizer, resolved.MessageKey, resolved.SolutionKey), resolved.Icon);
        }

        var result = LauncherUsbOperations.TryOpenDexTorHelp(context.DextorRootPath);
        if (!result.Success)
        {
            var solutionKey = result.ErrorSolutionKey ?? resolved.SolutionKey;
            return (
                BuildActionMessage(localizer, resolved.MessageKey, solutionKey)
                    + BuildErrorSuffix(localizer, result.ErrorMessage),
                MessageBoxIcon.Warning);
        }

        return (localizer.T("launcher.action.open.result"), MessageBoxIcon.Information);
    }

    private static (string Message, MessageBoxIcon Icon) BuildEjectActionResult(LauncherContext context, ILocalizer localizer)
    {
        var resolved = ResolveEjectMessage(context);
        if (resolved.MessageKey != "launcher.action.eject.prompt")
        {
            return (BuildActionMessage(localizer, resolved.MessageKey, resolved.SolutionKey), resolved.Icon);
        }

        var result = LauncherUsbOperations.TryStartEjectFlow(context.DextorRootPath);
        if (!result.Success)
        {
            var solutionKey = result.ErrorSolutionKey ?? resolved.SolutionKey;
            return (
                BuildActionMessage(localizer, resolved.MessageKey, solutionKey)
                    + BuildErrorSuffix(localizer, result.ErrorMessage),
                MessageBoxIcon.Warning);
        }

        var successMessage = BuildActionMessage(localizer, resolved.MessageKey, resolved.SolutionKey);
        var openedMessage = result.ErrorMessage is null
            ? localizer.T("launcher.action.eject.result.manual_eject_opened")
            : localizer.T(result.ErrorMessage);
        var message = string.IsNullOrWhiteSpace(successMessage)
            ? openedMessage
            : successMessage + Environment.NewLine + Environment.NewLine + openedMessage;

        return (message, MessageBoxIcon.Information);
    }

    private static string? ResolveUsbForCreation(LauncherContext context, ILocalizer localizer)
    {
        if (context.UsbRootCandidates.Count == 1)
        {
            return context.UsbRootPath;
        }

        if (context.UsbRootCandidates.Count == 0)
        {
            return null;
        }

        using var selector = new UsbSelectionWindow(context.UsbRootCandidates, localizer);
        var result = selector.ShowDialog();
        return result == DialogResult.OK ? selector.SelectedUsbRootPath : null;
    }

    private static int HandleUnavailable(ILocalizer localizer)
    {
        return ShowActionMessage(localizer.T("launcher.action.unavailable"), localizer);
    }


    private static string BuildActionMessage(
        ILocalizer localizer,
        string messageKey,
        string? solutionKey)
    {
        return BuildActionMessageWithArgs(localizer, messageKey, solutionKey);
    }

    private static string BuildActionMessageWithArgs(
        ILocalizer localizer,
        string messageKey,
        string? solutionKey,
        IReadOnlyDictionary<string, string>? messageArgs = null,
        IReadOnlyDictionary<string, string>? solutionArgs = null)
    {
        var message = localizer.T(messageKey, messageArgs);
        if (string.IsNullOrWhiteSpace(solutionKey))
        {
            return message;
        }

        return $"{message}{Environment.NewLine}{Environment.NewLine}{localizer.T("launcher.solution.label")} {localizer.T(solutionKey, solutionArgs)}";
    }

    private static string BuildErrorSuffix(ILocalizer localizer, string? errorMessageKey)
    {
        if (string.IsNullOrWhiteSpace(errorMessageKey))
        {
            return string.Empty;
        }

        return $"{Environment.NewLine}{Environment.NewLine}{localizer.T(errorMessageKey)}";
    }

    private static int ShowActionMessage(string message, ILocalizer localizer, MessageBoxIcon icon = MessageBoxIcon.Information)
    {
        ActionMessageWindow.Show(localizer, message, icon);
        return 0;
    }

    private static void OpenPrepareLinks(PreflightReport preflight)
    {
        var actionUrl = preflight.Requirements
            .FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.ActionUrl))
            ?.ActionUrl;

        if (!string.IsNullOrWhiteSpace(actionUrl))
        {
            OpenUrlCallback(actionUrl);
        }
    }

    private static void OpenUrl(string url)
    {
        var info = new ProcessStartInfo(url)
        {
            UseShellExecute = true
        };

        _ = Process.Start(info);
    }
}
