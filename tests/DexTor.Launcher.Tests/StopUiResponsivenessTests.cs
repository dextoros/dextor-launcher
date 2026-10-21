using System.Windows.Forms;
using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// The stop action must not block the UI thread while the session finalizes.
/// </summary>
/// <remarks>
/// The old path called <c>StopAsync().GetAwaiter().GetResult()</c> on the UI thread. That
/// is not merely slow: the session lifecycle resumes continuations on the captured
/// context, so blocking the pump that would run them is a deadlock, not a freeze.
/// </remarks>
public sealed class StopUiResponsivenessTests
{
    [Fact]
    public async Task TheStopHandlerReturnsControlInsteadOfBlockingTheUiThread()
    {
        // WinForms installs its own SynchronizationContext on handle creation and never
        // uninstalls it. Restored below so it cannot leak into later tests.
        var testContext = SynchronizationContext.Current;
        try
        {
            var finish = new TaskCompletionSource<SessionFinalizationReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var window = new LauncherWizardWindow(
                new TestLocalizer(),
                CreateDependencies(_ => finish.Task));
            window.CreateControl();
            _ = window.Handle;

            for (var step = 0; step < 5; step++)
            {
                await window.AdvanceAsync();
            }

            var stopping = window.AdvanceAsync();

            // The decisive fact: the handler came back while the session is still
            // finalizing. A GetAwaiter().GetResult() would not have returned here at all,
            // and the thread that would have been blocked is the one that has to run the
            // continuations the session lifecycle posts.
            stopping.IsCompleted.Should().BeFalse("the stop is still in flight");

            // The thread is free, so ordinary UI work still runs on it.
            System.Windows.Forms.Application.DoEvents();
            var primary = window.Controls.Find("WizardPrimaryButton", true).Single();
            primary.Enabled.Should().BeFalse("a destructive action in progress disables its own button");
            window.PageDetailText.Should().NotBeNull();

            finish.SetResult(new SessionFinalizationReport(
                WriterTermination: WriterTerminationResult.Confirmed,
                Journal: JournalPersistenceResult.Persisted));
            await stopping;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(testContext);
        }
    }

    [Fact]
    public async Task ASecondStopRequestDoesNotStartASecondSequence()
    {
        var testContext = SynchronizationContext.Current;
        try
        {
            var finish = new TaskCompletionSource<SessionFinalizationReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stops = 0;
            using var window = new LauncherWizardWindow(
                new TestLocalizer(),
                CreateDependencies(_ =>
                {
                    stops++;
                    return finish.Task;
                }));
            window.CreateControl();

            for (var step = 0; step < 5; step++)
            {
                await window.AdvanceAsync();
            }

            var first = window.AdvanceAsync();
            var second = window.AdvanceAsync();

            stops.Should().Be(1, "a destructive action must not be startable twice while it runs");

            finish.SetResult(new SessionFinalizationReport(
                WriterTermination: WriterTerminationResult.Confirmed,
                Journal: JournalPersistenceResult.Persisted));
            await Task.WhenAll(first, second);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(testContext);
        }
    }

    [Fact]
    public async Task AFailingStopIsReportedInsteadOfEscapingTheEventHandler()
    {
        var testContext = SynchronizationContext.Current;
        try
        {
            using var window = new LauncherWizardWindow(
                new TestLocalizer(),
                CreateDependencies(_ => Task.FromException<SessionFinalizationReport>(
                    new InvalidOperationException("the session could not be stopped"))));
            window.CreateControl();

            for (var step = 0; step < 5; step++)
            {
                await window.AdvanceAsync();
            }

            var act = () => window.AdvanceAsync();

            // An async void event handler that let this escape would take the process down.
            await act.Should().NotThrowAsync();
            window.PageDetailText.Should().Contain("could not be stopped");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(testContext);
        }
    }

    /// <summary>Echoes keys, so assertions name the key rather than a translation.</summary>
    private sealed class TestLocalizer : ILocalizer
    {
        public string T(string key, IReadOnlyDictionary<string, string>? args = null) =>
            args is null || args.Count == 0 ? key : key + " " + string.Join(" ", args.Values);

        public bool TryGetLanguage(out string language)
        {
            language = "en";
            return true;
        }
    }

    private static LauncherWizardDependencies CreateDependencies(
        Func<IProgress<SessionFinalizationStage>?, Task<SessionFinalizationReport>> stop) =>
        new()
        {
            RefreshEnvironment = () => Task.FromResult(new LauncherWizardEnvironment(
                new PreflightReport(true, []),
                new LauncherContext { IsWhpxEnabled = true })),
            PreparePc = () => Task.FromResult(new LauncherInlineActionResult(true, "Ready", "Restart")),
            RestartWindows = () => true,
            InspectImage = (_, _) => Task.FromResult(new LiveImageCacheInspectionResult(
                true, true, @"C:\Temp\DexTor\LiveImage", null, @"C:\Temp\DexTor\LiveImage\latest.iso", new string('c', 64), 1024)),
            PrepareImage = (_, _) => Task.FromResult(new LiveImagePreparationResult(true, null, null, null)),
            InspectUsbDrives = _ => Task.FromResult<IReadOnlyList<UsbDriveSnapshot>>(
                [new UsbDriveSnapshot(@"E:\", string.Empty, "exFAT", 64L * 1024 * 1024 * 1024, 48L * 1024 * 1024 * 1024, true, true)]),
            HasEnoughSpace = (_, _) => true,
            ConfirmErase = _ => true,
            CreateUsb = (snapshot, _) => Task.FromResult(new CreatorActionService.CreateUsbResult(
                true,
                snapshot.RootPath,
                new CreatorTransaction(CreatorStep.Complete))),
            VerifyDexTorUsb = (_, _, _) => Task.FromResult(new DexTorUsbReadinessResult(false)),
            StartDexTor = (_, progress) =>
            {
                progress.Report(new SessionStartProgress(SessionStartStage.Running));
                return Task.CompletedTask;
            },
            StopDexTor = stop,
            EjectDexTor = (_, _) => Task.FromResult(new SessionFinalizationReport(
                WriterTermination: WriterTerminationResult.Confirmed,
                DeviceRemoval: DeviceRemovalResult.Removed)),
            EmergencyStopDexTor = () => Task.CompletedTask,
            IsActiveUsbAvailable = _ => true,
            ShowEmergencyNotification = (_, _) => Task.CompletedTask,
            GetSessionState = () => SessionState.Running,
            GetSessionError = () => null
        };
}
