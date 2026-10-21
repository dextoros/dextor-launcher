using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.Qemu;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Session ownership belongs to <see cref="SessionHostService"/>, not to the launcher's
/// window. These tests drive the real window through teardown and recreation while a
/// stand-in QEMU runs, and assert the claim is untouched.
/// </summary>
/// <remarks>
/// In this build the session host is a component inside the launcher process rather than
/// a separate executable, so "UI restart" means the window is disposed and rebuilt while
/// the process, and with it the service that holds the claim, lives on. Ownership
/// surviving a process restart is a different guarantee and is not what this covers.
/// </remarks>
public sealed class SessionOwnershipUiLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dextor-ui-ownership-" + Guid.NewGuid().ToString("N"));
    private readonly string _usb;
    private readonly string _runtime;
    private readonly string _manifest;
    private readonly string _journal;
    private readonly string _dataImage;
    private readonly QemuStandIn _standIn;

    public SessionOwnershipUiLifetimeTests()
    {
        _usb = Path.Combine(_root, "usb");
        _runtime = Path.Combine(_root, "runtime");
        _journal = Path.Combine(_root, "session.json");
        _manifest = Path.Combine(_runtime, "qemu-manifest.json");

        var systemImage = CreateUsbFile(@"DEXTOR\system\slot-a\system.img", "dextor-system-image");
        _dataImage = CreateUsbFile(@"DEXTOR\storage\data.img", new string('0', 4096));

        Directory.CreateDirectory(Path.Combine(_usb, "DEXTOR", "metadata"));
        File.WriteAllText(
            Path.Combine(_usb, "DEXTOR", "metadata", "device.json"),
            JsonSerializer.Serialize(new
            {
                state = "ready",
                format = 3,
                requestId = "aaaabbbb-cccc-dddd-eeee-ffff00001111",
                systemImageSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(systemImage))),
                systemImagePath = @"DEXTOR\system\slot-a\system.img",
                dataImagePath = @"DEXTOR\storage\data.img",
                storageState = "storage_not_initialized"
            }));

        // A real executable, started by the production launcher into the production job.
        _standIn = QemuStandIn.Install(_runtime, _manifest);
    }

    [Fact]
    public async Task DisposingAndRecreatingTheWindowDoesNotReleaseARunningSession()
    {
        await using var service = new SessionHostService(null, _runtime, _manifest, _journal, null);
        await service.StartAsync(_usb);
        service.State.Should().Be(SessionState.Running);
        var qemuId = _standIn.ReadProcessId();

        // Creating a WinForms handle installs WindowsFormsSynchronizationContext on this
        // thread and never uninstalls it, not even when the form is disposed. In the real
        // launcher that is correct, because Application.Run pumps. Here nothing pumps, so
        // a later await would post its continuation to a message loop that never runs.
        var testContext = SynchronizationContext.Current;
        try
        {
            // The user closes the launcher window and opens it again. The VM keeps
            // running, so the claim on the device must be untouched by either event.
            var window = new LauncherWizardWindow(new Localizer("en"), CreateDependencies(service));
            _ = window.Handle;
            window.Dispose();
            OwnershipIsFree().Should().BeFalse("closing the window does not end the VM session");

            using var reopened = new LauncherWizardWindow(new Localizer("en"), CreateDependencies(service));
            _ = reopened.Handle;

            OwnershipIsFree().Should().BeFalse("reopening the window must not disturb the live claim");
            ProcessHasTerminated(qemuId).Should().BeFalse();
            service.State.Should().Be(SessionState.Running);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(testContext);
        }
    }

    [Fact]
    public async Task ShuttingDownTheSessionHostEndsTheWriterAndTheClaimTogether()
    {
        var service = new SessionHostService(null, _runtime, _manifest, _journal, null);
        await service.StartAsync(_usb);
        var qemuId = _standIn.ReadProcessId();
        OwnershipIsFree().Should().BeFalse();

        // Whatever ends the session host — a clean exit or a crash — must not leave a
        // writer behind for the next launcher, which would find the device free.
        await service.DisposeAsync();

        ProcessHasTerminated(qemuId).Should().BeTrue("the job object terminates the writer");
        OwnershipIsFree().Should().BeTrue();
    }

    private bool OwnershipIsFree()
    {
        var free = false;
        var probe = new Thread(() =>
        {
            var claim = DexTorSessionLock.TryAcquire(DexTorSessionIdentity.ForDataImage(_dataImage));
            free = claim.IsGranted;
            claim.Lock?.Dispose();
        })
        {
            IsBackground = true
        };
        probe.Start();
        probe.Join();
        return free;
    }

    private static bool ProcessHasTerminated(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }


    private string CreateUsbFile(string relativePath, string content)
    {
        var path = Path.Combine(_usb, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// The real dependency surface the window is given in <c>Program.Main</c>, wired to
    /// the live service so nothing about the window is a stand-in for the session host.
    /// </summary>
    private LauncherWizardDependencies CreateDependencies(SessionHostService service) =>
        new()
        {
            RefreshEnvironment = () => Task.FromResult(new LauncherWizardEnvironment(
                new PreflightReport(true, []),
                new LauncherContext { IsWhpxEnabled = true })),
            PreparePc = () => Task.FromResult(new LauncherInlineActionResult(true, "Ready", "Restart")),
            RestartWindows = () => true,
            InspectImage = (_, _) => Task.FromResult(new LiveImageCacheInspectionResult(
                true, false, Path.Combine(_root, "cache"), null, null, null)),
            PrepareImage = (_, _) => Task.FromResult(new LiveImagePreparationResult(true, null, null, null)),
            InspectUsbDrives = _ => Task.FromResult<IReadOnlyList<UsbDriveSnapshot>>([]),
            HasEnoughSpace = (_, _) => true,
            ConfirmErase = _ => false,
            CreateUsb = (snapshot, _) => Task.FromResult(new CreatorActionService.CreateUsbResult(
                true,
                snapshot.RootPath,
                new CreatorTransaction(CreatorStep.Complete))),
            VerifyDexTorUsb = (_, _, _) => Task.FromResult(new DexTorUsbReadinessResult(false)),
            StartDexTor = (path, progress) => service.StartAsync(path, progress),
            StopDexTor = progress => service.StopAsync(progress),
            EjectDexTor = (path, progress) => service.EjectAsync(path, progress),
            EmergencyStopDexTor = () => service.EmergencyStopAsync(),
            IsActiveUsbAvailable = _ => true,
            ShowEmergencyNotification = (_, _) => Task.CompletedTask,
            GetSessionState = () => service.State,
            GetSessionError = () => service.LastError
        };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // A stand-in process may still hold the directory; the temp folder is disposable.
        }
    }
}
