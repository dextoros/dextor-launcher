using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.SessionHost;

using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Walks the wizard through every screen of the design and saves each one - the client area,
/// as the mock-ups show it - as a PNG named after the mock-up it is compared with. Runs only
/// when <c>DEXTOR_SCREENSHOTS</c> names a folder; otherwise it does nothing.
/// <c>DEXTOR_SCREENSHOTS_ONLY</c> limits it to names containing that text.
/// </summary>
public sealed class WizardScreenshots
{
    private static readonly long GB = 1024L * 1024 * 1024;

    [Fact]
    public async Task SaveEveryStep()
    {
        var folder = Environment.GetEnvironmentVariable("DEXTOR_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        // The windows live on a thread with a message loop of their own, so every await
        // comes back to it - as in the real launcher.
        var finished = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            // As the launcher runs (SystemAware), so the pictures are at the display's scale.
            WizardDesign.UseSystemDpiOnThisThread();
            var context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(context);
            context.Post(async _ =>
            {
                try
                {
                    await CaptureAsync(folder);
                    finished.SetResult();
                }
                catch (Exception exception)
                {
                    finished.SetException(exception);
                }
                finally
                {
                    System.Windows.Forms.Application.ExitThread();
                }
            }, null);
            System.Windows.Forms.Application.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task;
    }

    private static async Task CaptureAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        var language = Environment.GetEnvironmentVariable("DEXTOR_SCREENSHOTS_LANGUAGE") ?? "ru";
        var only = Environment.GetEnvironmentVariable("DEXTOR_SCREENSHOTS_ONLY");
        bool Wanted(string name) => string.IsNullOrWhiteSpace(only) || name.Contains(only, StringComparison.OrdinalIgnoreCase);

        async Task Run(string name, Action<Dependencies> configure, Func<LauncherWizardWindow, Func<string, Task>, Task> drive)
        {
            if (!Wanted(name)) return;
            var dependencies = new Dependencies();
            configure(dependencies);
            using var window = new LauncherWizardWindow(new Localizer(language), dependencies.Build())
            {
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-4000, 0)
            };
            window.Show();
            await drive(window, async id => await ShotAsync(window, Path.Combine(folder, id + ".png")));
            window.Hide();
        }

        await Run("220042", _ => { }, async (w, shot) => await shot("220042"));
        await Run("220057", d => d.Environment = new TaskCompletionSource<LauncherWizardEnvironment>(), async (w, shot) =>
        {
            var advance = w.AdvanceAsync();
            await Pump(600);
            await shot("220057");
        });
        await Run("220105", _ => { }, async (w, shot) =>
        {
            await w.AdvanceAsync();
            await shot("220105");
        });
        await Run("220114", d => d.VirtualizationOff = true, async (w, shot) =>
        {
            await w.AdvanceAsync();
            await shot("220114");
        });
        await Run("220120", d => d.ImageReady = false, async (w, shot) =>
        {
            await w.AdvanceAsync();
            await w.AdvanceAsync();
            await shot("220120");
        });
        foreach (var (id, key, args) in new (string, string, Dictionary<string, string>?)[]
                 {
                     ("220127", LiveImagePreparationService.DownloadFailedKey, null),
                     ("220135", LiveImagePreparationService.SignatureFailedKey, null),
                     ("image-checksum", LiveImagePreparationService.ChecksumFailedKey, null),
                     ("220143", LiveImagePreparationService.NoSpaceKey, new Dictionary<string, string>
                     {
                         ["drive"] = "C:", ["needed_bytes"] = (2 * GB).ToString(System.Globalization.CultureInfo.InvariantCulture),
                         ["available_bytes"] = ((long)(0.8 * GB)).ToString(System.Globalization.CultureInfo.InvariantCulture)
                     })
                 })
        {
            await Run(id, d =>
            {
                d.ImageReady = false;
                d.Prepare = (_, _) => Task.FromResult(new LiveImagePreparationResult(false, null, null, null, 0, "HttpRequestException: No such host is known. (dextoros.com:443)", key, args));
            }, async (w, shot) =>
            {
                await w.AdvanceAsync();
                await w.AdvanceAsync();
                await w.AdvanceAsync();
                await shot(id);
            });
        }

        await Run("220322", d =>
        {
            d.ImageReady = false;
            var hold = new TaskCompletionSource<LiveImagePreparationResult>();
            d.Prepare = async (_, progress) =>
            {
                var total = (long)(1.6 * GB);
                for (var i = 1; i <= 24; i++)
                {
                    progress.Report(new LiveImagePreparationProgress(i * 10_700_000L, total, 65.4 * 1024 * 1024, TimeSpan.FromSeconds(21), LiveImagePreparationService.StageDownload));
                    await Task.Delay(60);
                }

                return await hold.Task;
            };
        }, async (w, shot) =>
        {
            await w.AdvanceAsync();
            await w.AdvanceAsync();
            _ = w.AdvanceAsync();
            await Pump(2400);
            await shot("220322");
        });
        await Run("220510", _ => { }, async (w, shot) =>
        {
            await w.AdvanceAsync();
            await w.AdvanceAsync();
            await shot("220510");
            Click(w, "WizardDetailsToggle");
            await shot("220521");
        });
        await Run("220543", _ => { }, async (w, shot) =>
        {
            await AdvanceTo(w, 3);
            await shot("220543");
            using var dialog = new LauncherWizardWindow.ConfirmationDialog(new Localizer(language),
                new Localizer(language).T("launcher.wizard.usb.confirm_erase", new Dictionary<string, string>
                {
                    ["drive"] = @"F:\", ["disk"] = "3", ["model"] = "Kingston DataTraveler 3.0", ["capacity"] = "57,8 ГБ"
                }),
                new Localizer(language).T("launcher.wizard.usb.erase_action"), new Localizer(language).T("launcher.wizard.dialog.cancel"), destructive: true);
            await DialogShotAsync(w, dialog, Path.Combine(folder, "220553.png"));
        });
        await Run("220549", d => d.Drives = [], async (w, shot) =>
        {
            await AdvanceTo(w, 3);
            await shot("220549");
        });
        // Not in the mock-ups: a slow stick, the tallest USB page - it must fit without scroll bars.
        await Run("usb-slow", d => d.Drives =
        [
            new UsbDriveSnapshot(@"Z:\", "", "FAT32", 7_838_000_000, 5_905_000_000, true, true,
                new UsbPhysicalIdentity(5, "id-z", "serial-z", 7_838_000_000, "USB", "JetFlash Transcend 8GB", false, false, false),
                AccessSpeed: new UsbAccessSpeedResult(6.0, 25.3))
        ], async (w, shot) =>
        {
            await AdvanceTo(w, 3);
            await shot("usb-slow");
        });
        await Run("220600", d => d.Drives =
        [
            new UsbDriveSnapshot(@"E:\", "WD Elements", "NTFS", (long)(1.8 * 1024 * GB), (long)(1.2 * 1024 * GB), true, true,
                new UsbPhysicalIdentity(4, "id-e", "serial-e", (long)(1.8 * 1024 * GB), "USB", "WD Elements", false, false, false), IsExternalDisk: true)
        ], async (w, shot) =>
        {
            await AdvanceTo(w, 3);
            await shot("220600");
        });
        foreach (var (id, stage, done) in new[] { ("220613", UsbProvisioningStage.CleaningDisk, 0L), ("220620", UsbProvisioningStage.WritingSystemImage, 131_100_000L) })
        {
            await Run(id, d =>
            {
                d.Create = async (_, progress) =>
                {
                    for (var i = 0; i < 12; i++)
                    {
                        progress.Report(new CreatorActionService.CreatorStepProgress(
                            stage == UsbProvisioningStage.CleaningDisk ? CreatorStep.FormatUsb : CreatorStep.WriteSystemImage,
                            CreatorActionService.CreatorStepStatus.Info, UsbProvisionerClient.StageMessageKey(stage),
                            BytesDone: done == 0 ? 0 : done / 12 * (i + 1), BytesTotal: done == 0 ? 0 : (long)(1.6 * GB)));
                        await Task.Delay(80);
                    }

                    return await new TaskCompletionSource<CreatorActionService.CreateUsbResult>().Task;
                };
            }, async (w, shot) =>
            {
                await AdvanceTo(w, 3);
                _ = w.AdvanceAsync();
                await Pump(1600);
                await shot(id);
                if (id == "220620")
                {
                    var localizer = new Localizer(language);
                    using var dialog = new LauncherWizardWindow.ConfirmationDialog(localizer, localizer.T("launcher.wizard.create.abort_confirm"),
                        localizer.T("launcher.wizard.create.abort_short"), localizer.T("launcher.wizard.create.abort_keep"), destructive: true,
                        LauncherWizardWindow.ConfirmationStyle.Abort);
                    await DialogShotAsync(w, dialog, Path.Combine(folder, "220626.png"));
                }
            });
        }

        await Run("create-verify", d => d.Create = async (_, progress) =>
        {
            // The copy for Windows being read back: "Checking the write" is the line under way.
            foreach (var (stage, bytesDone, bytesTotal) in new[]
                     {
                         (UsbProvisioningStage.WritingSystemImage, 2 * (long)(1.6 * GB), 2 * (long)(1.6 * GB)),
                         (UsbProvisioningStage.CreatingPrivateStorage, 0L, 0L),
                         (UsbProvisioningStage.CopyingWindowsImage, (long)(1.9 * GB), 2 * (long)(1.6 * GB))
                     })
            {
                progress.Report(new CreatorActionService.CreatorStepProgress(UsbProvisionerClient.MapStage(stage),
                    CreatorActionService.CreatorStepStatus.Info, UsbProvisionerClient.StageMessageKey(stage), BytesDone: bytesDone, BytesTotal: bytesTotal));
                await Task.Delay(120);
            }

            return await new TaskCompletionSource<CreatorActionService.CreateUsbResult>().Task;
        }, async (w, shot) =>
        {
            await AdvanceTo(w, 3);
            _ = w.AdvanceAsync();
            await Pump(1200);
            await shot("create-verify");
        });
        await Run("220631", d => d.Create = async (_, progress) =>
        {
            progress.Report(new CreatorActionService.CreatorStepProgress(CreatorStep.WriteSystemImage, CreatorActionService.CreatorStepStatus.Info,
                UsbProvisionerClient.StageMessageKey(UsbProvisioningStage.WritingSystemImage), BytesDone: 200_000_000, BytesTotal: (long)(1.6 * GB)));
            await Task.Delay(200);
            return new CreatorActionService.CreateUsbResult(false, @"F:\", new CreatorTransaction(CreatorStep.WriteSystemImage), "device gone",
                ErrorMessageKey: "launcher.create.error.device.disappeared");
        }, async (w, shot) =>
        {
            await AdvanceTo(w, 4);
            await shot("220631");
        });
        await Run("220636", _ => { }, async (w, shot) =>
        {
            await AdvanceTo(w, 4);
            await shot("220636");
        });
        await Run("220641", d => d.VirtualizationOff = true, async (w, shot) =>
        {
            await AdvanceTo(w, 4);
            await shot("220641");
        });
        await Run("220647", d => d.Start = (_, _) => new TaskCompletionSource().Task, async (w, shot) =>
        {
            await AdvanceTo(w, 4);
            _ = w.AdvanceAsync();
            await Pump(800);
            await shot("220647");
        });
        await Run("220651", d => d.Start = (_, _) => Task.FromException(new InvalidOperationException("QEMU exited: WHPX: No accelerator found.")), async (w, shot) =>
        {
            await AdvanceTo(w, 4);
            await w.AdvanceAsync();
            await shot("220651");
        });
    }

    private static async Task AdvanceTo(LauncherWizardWindow window, int presses)
    {
        for (var i = 0; i < presses; i++)
        {
            await window.AdvanceAsync();
            await Pump(50);
        }
    }

    private static void Click(Control root, string name)
    {
        var control = root.Controls.Find(name, true).Single();
        typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, [EventArgs.Empty]);
    }

    private static async Task Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds)
        {
            System.Windows.Forms.Application.DoEvents();
            await Task.Delay(15);
        }
    }

    /// <summary>The client area, without the window frame - as the mock-ups were cropped.</summary>
    private static async Task ShotAsync(LauncherWizardWindow window, string path)
    {
        // Long enough for the page cross-fade and the stepper's tick to finish.
        await Pump(700);
        var root = window.Controls.Find("WizardRoot", true).Single();
        using var bitmap = new Bitmap(root.Width, root.Height);
        root.DrawToBitmap(bitmap, new Rectangle(Point.Empty, root.Size));
        bitmap.Save(path, ImageFormat.Png);
        var lines = new List<string>();
        void Dump(Control control, int depth)
        {
            var at = root.PointToClient(control.PointToScreen(Point.Empty));
            lines.Add($"{new string(' ', depth * 2)}{control.GetType().Name} {control.Name} [{at.X},{at.Y} {control.Width}x{control.Height}] visible={control.Visible} \"{control.Text.Replace(Environment.NewLine, " | ", StringComparison.Ordinal)}\"");
            foreach (Control child in control.Controls) Dump(child, depth + 1);
        }

        Dump(root, 0);
        File.WriteAllLines(Path.ChangeExtension(path, ".txt"), lines);
    }

    private static async Task DialogShotAsync(LauncherWizardWindow window, LauncherWizardWindow.ConfirmationDialog dialog, string path)
    {
        dialog.CoverWindow(window);
        dialog.Show(window);
        await Pump(300);
        using var full = new Bitmap(dialog.Width, dialog.Height);
        dialog.DrawToBitmap(full, new Rectangle(Point.Empty, dialog.Size));
        var origin = window.PointToScreen(Point.Empty);
        var offset = new Point(origin.X - dialog.Left, origin.Y - dialog.Top);
        using var client = new Bitmap(window.ClientSize.Width, window.ClientSize.Height);
        using (var g = Graphics.FromImage(client))
        {
            g.DrawImage(full, new Rectangle(Point.Empty, client.Size), new Rectangle(offset, client.Size), GraphicsUnit.Pixel);
        }

        client.Save(path, ImageFormat.Png);
        dialog.Hide();
    }

    internal sealed class Dependencies
    {
        internal static LauncherWizardDependencies ForTests() => new Dependencies().Build();

        internal TaskCompletionSource<LauncherWizardEnvironment>? Environment { get; set; }
        internal bool VirtualizationOff { get; set; }
        internal bool ImageReady { get; set; } = true;
        internal Func<CancellationToken, IProgress<LiveImagePreparationProgress>, Task<LiveImagePreparationResult>>? Prepare { get; set; }
        internal IReadOnlyList<UsbDriveSnapshot>? Drives { get; set; }
        internal Func<UsbDriveSnapshot, IProgress<CreatorActionService.CreatorStepProgress>, Task<CreatorActionService.CreateUsbResult>>? Create { get; set; }
        internal Func<string, IProgress<SessionStartProgress>, Task>? Start { get; set; }

        internal LauncherWizardDependencies Build()
        {
            var clean = new SessionFinalizationReport(
                GuestShutdown: GuestShutdownEvidence.ObservedGuestInitiated,
                Escalation: ShutdownEscalation.PowerButton,
                WriterTermination: WriterTerminationResult.Confirmed,
                Flush: HostFlushResult.Succeeded,
                Journal: JournalPersistenceResult.Persisted);
            var usb = new UsbDriveSnapshot(@"F:\", "", "exFAT", 62_062_000_000, 61_955_000_000, true, true,
                new UsbPhysicalIdentity(3, "id", "serial", 62_062_000_000, "USB", "Kingston DataTraveler 3.0", false, false, false));
            var image = @"C:\DexTor\LiveImage\dextor-os-1.0-beta.img";
            const string sha = "9f2c41ab7e05c3d81b6a92f4e0175cd38a2e6f19b4c073d5e8a12f6c0b94d7e3";
            var report = new PreflightReport(true,
            [
                new PreflightRequirement(PreflightRequirementKind.OperatingSystem, PreflightRequirementState.Ok, "preflight.os.title", "preflight.os.ok",
                    new Dictionary<string, string> { ["product"] = "Windows 11 Pro", ["release"] = "24H2", ["build"] = "26200" }),
                new PreflightRequirement(PreflightRequirementKind.ProcessorArchitecture, PreflightRequirementState.Ok, "preflight.arch.title", "preflight.arch.ok"),
                new PreflightRequirement(PreflightRequirementKind.Memory, PreflightRequirementState.Ok, "preflight.memory.title", "preflight.memory.ok",
                    new Dictionary<string, string> { ["bytes"] = (16 * GB).ToString(System.Globalization.CultureInfo.InvariantCulture) }),
                new PreflightRequirement(PreflightRequirementKind.DiskSpace, PreflightRequirementState.Ok, "preflight.disk.title", "preflight.disk.ok",
                    new Dictionary<string, string> { ["bytes"] = (120 * GB).ToString(System.Globalization.CultureInfo.InvariantCulture), ["drive"] = "C:" }),
                new PreflightRequirement(PreflightRequirementKind.HostVirtualization,
                    VirtualizationOff ? PreflightRequirementState.Warning : PreflightRequirementState.Ok, "preflight.virtualization.title", "preflight.virtualization.ok")
            ]);
            return new LauncherWizardDependencies
            {
                RefreshEnvironment = () => Environment?.Task ?? Task.FromResult(new LauncherWizardEnvironment(report, new LauncherContext { IsWhpxEnabled = !VirtualizationOff })),
                PreparePc = () => Task.FromResult(new LauncherInlineActionResult(true, "Ready", "Restart")),
                RestartWindows = () => true,
                InspectImage = (_, _) => Task.FromResult(new LiveImageCacheInspectionResult(true, ImageReady, @"C:\DexTor\LiveImage", "https://dextoros.com/releases/dextor-os-1.0-beta.img",
                    image, ImageReady ? sha : null, (long)(1.6 * GB), SigningKey: ImageReady ? "DexTor OS Release · " + DexTor.Release.ImageSigningKey.Release.Fingerprint : null)),
                PrepareImage = Prepare ?? ((_, _) => Task.FromResult(new LiveImagePreparationResult(true, null, image, sha, (long)(1.6 * GB), SigningKey: "DexTor OS Release · " + DexTor.Release.ImageSigningKey.Release.Fingerprint))),
                InspectUsbDrives = _ => Task.FromResult(Drives ?? [usb]),
                HasEnoughSpace = (_, _) => true,
                ConfirmErase = _ => true,
                CreateUsb = Create ?? ((drive, _) => Task.FromResult(new CreatorActionService.CreateUsbResult(true, drive.RootPath, new CreatorTransaction(CreatorStep.Complete)))),
                VerifyDexTorUsb = (_, _, _) => Task.FromResult(new DexTorUsbReadinessResult(false)),
                StartDexTor = Start ?? ((_, _) => Task.CompletedTask),
                StopDexTor = _ => Task.FromResult(clean),
                EjectDexTor = (_, _) => Task.FromResult(clean),
                EmergencyStopDexTor = () => Task.CompletedTask,
                IsActiveUsbAvailable = _ => true,
                ShowEmergencyNotification = (_, _) => Task.CompletedTask,
                GetSessionState = () => SessionState.Running,
                GetSessionError = () => null
            };
        }
    }
}
