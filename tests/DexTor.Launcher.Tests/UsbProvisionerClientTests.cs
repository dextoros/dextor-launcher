using FluentAssertions;
using Xunit;

using DexTor.Contracts;
using DexTor.Creator;

namespace DexTor.Launcher.Tests;

public sealed class UsbProvisionerClientTests
{
    [Fact]
    public void TheJournalIsKeptInTheLauncherUsersFolderWhoeverTheElevatedProcessRunsAs()
    {
        var startInfo = UsbProvisionerClient.BuildStartInfo(
            @"C:\Temp\request.json", @"C:\Temp\result.json", @"C:\Temp\progress.json",
            Path.Combine(Path.GetTempPath(), "DexTorOS-Portable-win-x64.exe"),
            UsbProvisionerClient.JournalDirectory);

        startInfo.Arguments.Should().Contain($"--journal \"{UsbProvisionerClient.JournalDirectory}\"");
        UsbProvisionerClient.JournalDirectory.Should().Be(DexTorStorage.Journal);
    }

    [Fact]
    public void TheProvisionersOwnAccountOfAFailureLandsInTheJournal()
    {
        var journal = Path.Combine(Path.GetTempPath(), "dextor-journal-" + Guid.NewGuid().ToString("N") + ".log");
        try
        {
            UsbProvisionerClient.RecordDiagnostic(journal, "Unspecified at WritingSystemImage: System.IO.IOException hresult=0x80070001: Incorrect function.");
            UsbProvisionerClient.RecordDiagnostic(journal, null);

            File.ReadAllLines(journal).Should().ContainSingle()
                .Which.Should().Contain("result:").And.Contain("WritingSystemImage").And.Contain("0x80070001");
        }
        finally
        {
            File.Delete(journal);
        }
    }

    [Fact]
    public void BytesWithinOneStageReachTheWizardNotOnlyStageChanges()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor-progress-" + Guid.NewGuid().ToString("N") + ".json");
        var requestId = Guid.NewGuid();
        var reports = new List<CreatorActionService.CreatorStepProgress>();
        var sink = new InlineSink(reports.Add);
        var cursor = new UsbProvisionerClient.ProgressCursor();
        try
        {
            void Write(long done, int second) => File.WriteAllText(path, UsbProvisioningSerializer.SerializeProgress(
                new UsbProvisioningProgress(requestId, UsbProvisioningStage.WritingSystemImage,
                    new DateTimeOffset(2026, 9, 24, 12, 0, second, TimeSpan.Zero), done, 4000)));

            Write(1000, 1);
            UsbProvisionerClient.ReportProgressFile(path, requestId, cursor, sink);
            UsbProvisionerClient.ReportProgressFile(path, requestId, cursor, sink);
            Write(2000, 2);
            UsbProvisionerClient.ReportProgressFile(path, requestId, cursor, sink);

            reports.Select(report => report.BytesDone).Should().Equal(1000, 2000);
            reports.Should().OnlyContain(report => report.BytesTotal == 4000 && report.Step == CreatorStep.WriteSystemImage);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class InlineSink(Action<CreatorActionService.CreatorStepProgress> report) : IProgress<CreatorActionService.CreatorStepProgress>
    {
        public void Report(CreatorActionService.CreatorStepProgress value) => report(value);
    }

    [Fact]
    public void AFaultyStickIsNamedWithWhereTheWriteStopped()
    {
        var result = new UsbProvisioningResult(Guid.NewGuid(), UsbProvisioningStatus.InterruptedAfterDiskChange,
            "The selected USB is present but stopped accepting data.", Failure: UsbProvisioningFailure.DeviceFaulty,
            FailureOffsetBytes: 1_395_864_371);

        var failure = UsbProvisionerClient.BuildProvisioningFailure(result, @"E:\", CreatorActionService.CreateTransaction());

        failure.ErrorMessageKey.Should().Be("launcher.create.error.device.faulty_at");
        failure.ErrorMessageArgs!["at"].Should().Be("1.3 GB");
        CreatorActionService.GetCreateErrorSolutionKey(failure.ErrorMessageKey).Should().Be("launcher.action.create.solution.device.faulty_at");
        foreach (var language in new[] { "en", "ru", "uk" })
        {
            var localizer = new DexTor.Application.Localizer(language);
            localizer.T(failure.ErrorMessageKey!, failure.ErrorMessageArgs).Should().Contain("1.3 GB");
            localizer.T("launcher.action.create.solution.device.faulty_at").Should().Contain("H2testw");
        }
    }
    [Fact]
    public void PortableLauncherRestartsItselfElevatedInsteadOfShippingASecondExecutable()
    {
        var launcher = Path.Combine(Path.GetTempPath(), "DexTorOS-Portable-win-x64.exe");

        var startInfo = UsbProvisionerClient.BuildStartInfo(
            @"C:\Temp\request.json",
            @"C:\Temp\result.json",
            @"C:\Temp\progress.json",
            launcher);

        startInfo.FileName.Should().Be(launcher);
        startInfo.Verb.Should().Be("runas");
        startInfo.UseShellExecute.Should().BeTrue();
        startInfo.Arguments.Should().StartWith(DexTor.Provisioner.ProvisionerCommand.ModeArgument + " ");
        startInfo.Arguments.Should().Contain("--request \"C:\\Temp\\request.json\"");
        startInfo.Arguments.Should().Contain("--execute");
    }

    [Fact]
    public async Task ProvisionModeWithoutExecuteSwitchWritesARejectionAndChangesNothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dextor-provision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var request = Path.Combine(directory, "request.json");
            var result = Path.Combine(directory, "result.json");
            await File.WriteAllTextAsync(request, "{\"requestId\":\"00000000-0000-0000-0000-000000000001\"}");

            var exitCode = await DexTor.Provisioner.ProvisionerCommand.RunAsync(["--request", request, "--result", result]);

            exitCode.Should().Be(1);
            UsbProvisioningSerializer.DeserializeResult(await File.ReadAllTextAsync(result))!
                .Status.Should().Be(UsbProvisioningStatus.Rejected);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ElevatedCommandUsesRequestResultFilesAndExplicitExecuteSwitch()
    {
        var startInfo = UsbProvisionerClient.BuildStartInfo(
            @"C:\Temp\request.json",
            @"C:\Temp\result.json",
            @"C:\Temp\progress.json");

        startInfo.UseShellExecute.Should().BeTrue();
        startInfo.Verb.Should().Be("runas");
        startInfo.WindowStyle.Should().Be(System.Diagnostics.ProcessWindowStyle.Hidden);
        startInfo.Arguments.Should().Contain("--request \"C:\\Temp\\request.json\"");
        startInfo.Arguments.Should().Contain("--result \"C:\\Temp\\result.json\"");
        startInfo.Arguments.Should().Contain("--progress \"C:\\Temp\\progress.json\"");
        startInfo.Arguments.Should().Contain("--execute");
    }

    [Fact]
    public void ProvisionerProgressStagesMapToVisibleCreatorStepsAndLocalizedMessages()
    {
        UsbProvisionerClient.MapStage(UsbProvisioningStage.CleaningDisk).Should().Be(CreatorStep.FormatUsb);
        UsbProvisionerClient.MapStage(UsbProvisioningStage.WritingSystemImage).Should().Be(CreatorStep.WriteSystemImage);
        UsbProvisionerClient.MapStage(UsbProvisioningStage.CreatingPrivateStorage).Should().Be(CreatorStep.CreateDataImage);
        UsbProvisionerClient.MapStage(UsbProvisioningStage.Verifying).Should().Be(CreatorStep.VerifyUsb);
        UsbProvisionerClient.StageMessageKey(UsbProvisioningStage.WaitingForDataVolume)
            .Should().Be("launcher.wizard.create.provisioner_waitingfordatavolume");
    }

    [Theory]
    [InlineData(UsbProvisioningStatus.FailedBeforeDiskChange, "launcher.create.error.safety.unchanged")]
    [InlineData(UsbProvisioningStatus.InterruptedAfterDiskChange, "launcher.create.error.safety.possibly_modified")]
    public void ProvisioningFailureIsNotReportedAsAReleaseError(
        UsbProvisioningStatus status,
        string expectedSafetyKey)
    {
        var result = new UsbProvisioningResult(Guid.NewGuid(), status, "The device is not ready.");

        var mapped = UsbProvisionerClient.BuildProvisioningFailure(
            result,
            @"PhysicalDrive7",
            CreatorActionService.CreateTransaction());

        mapped.ErrorMessageKey.Should().Be("launcher.create.error.provisioning");
        mapped.ErrorMessageArgs.Should().Contain("error", "The device is not ready.");
        mapped.ErrorSafetyMessageKey.Should().Be(expectedSafetyKey);
    }

    [Fact]
    public void ProvisionerUsesAlreadyPreparedLocalImageInsteadOfEmptyReleaseCachePath()
    {
        var image = Path.GetTempFileName();
        try
        {
            var config = new CreatorActionService.ReleaseConfig(
                image,
                new string('a', 64),
                Path.Combine(Path.GetTempPath(), "DexTor", "Release", "system.img"));

            UsbProvisionerClient.ResolveSystemImagePath(config).Should().Be(image);
        }
        finally
        {
            File.Delete(image);
        }
    }

    [Fact]
    public async Task MissingPreparedImageIncludesErrorArgumentForLocalizedMessage()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.iso");
        var usb = new UsbDriveSnapshot(
            string.Empty,
            string.Empty,
            "RAW",
            64L * 1024 * 1024 * 1024,
            null,
            true,
            true,
            new UsbPhysicalIdentity(7, "id", "serial", 64L * 1024 * 1024 * 1024, "USB", "Test USB", false, false, false));

        var result = await UsbProvisionerClient.CreateAsync(
            usb,
            missing,
            new string('a', 64),
            new Progress<CreatorActionService.CreatorStepProgress>());

        result.Success.Should().BeFalse();
        result.ErrorMessageKey.Should().Be("launcher.create.error.release");
        result.ErrorMessageArgs.Should().ContainKey("error");
        result.ErrorMessageArgs!["error"].Should().NotContain("{error}");
    }

    [Fact]
    public async Task StoppingAskedForIsPassedToTheElevatedProvisionerAndEndsItsRun()
    {
        var marker = Path.Combine(Path.GetTempPath(), "dextor-cancel-" + Guid.NewGuid().ToString("N"));
        using var cancellation = new CancellationTokenSource();
        try
        {
            UsbProvisionerClient.BuildStartInfo(@"C:Tempequest.json", @"C:Tempesult.json", cancelPath: marker)
                .Arguments.Should().Contain($"--cancel \"{marker}\"");
            using var watch = DexTor.Provisioner.ProvisionerCommand.WatchForCancelFile(marker, cancellation);
            await Task.Delay(400);
            cancellation.IsCancellationRequested.Should().BeFalse();

            await File.WriteAllTextAsync(marker, string.Empty);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!cancellation.IsCancellationRequested && DateTime.UtcNow < deadline) await Task.Delay(50);

            cancellation.IsCancellationRequested.Should().BeTrue();
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Theory]
    [InlineData(UsbProvisioningStatus.InterruptedAfterDiskChange, "launcher.create.error.safety.possibly_modified")]
    [InlineData(UsbProvisioningStatus.FailedBeforeDiskChange, "launcher.create.error.safety.unchanged")]
    public void AStoppedCreationIsSaidToBeStoppedNotFailed(UsbProvisioningStatus status, string expectedSafety)
    {
        var failure = UsbProvisionerClient.BuildProvisioningFailure(
            new UsbProvisioningResult(Guid.NewGuid(), status, "USB creation was cancelled."), @"E:\", CreatorActionService.CreateTransaction());

        var stopped = UsbProvisionerClient.AsCancelled(failure);

        stopped.ErrorMessageKey.Should().Be("launcher.create.error.cancelled");
        stopped.ErrorSafetyMessageKey.Should().Be(expectedSafety);
        CreatorActionService.GetCreateErrorSolutionKey(stopped.ErrorMessageKey).Should().Be("launcher.action.create.solution.cancelled");
        foreach (var language in new[] { "en", "ru", "uk" })
        {
            var localizer = new DexTor.Application.Localizer(language);
            foreach (var key in new[] { "launcher.create.error.cancelled", "launcher.action.create.solution.cancelled", "launcher.wizard.create.abort_confirm" })
            {
                localizer.T(key).Should().NotBe(key);
            }
        }
    }

    [Fact]
    public void NothingToStopWhenNoCreationIsRunning()
    {
        UsbProvisionerClient.RequestCancel().Should().BeFalse();
    }
}
