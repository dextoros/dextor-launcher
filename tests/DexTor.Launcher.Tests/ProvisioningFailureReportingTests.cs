using DexTor.Application;
using DexTor.Contracts;
using DexTor.Creator;
using DexTor.Launcher;

using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// What the operator is told after a failed creation: which kind of fault it was, and what
/// is actually known about the drive.
/// </summary>
/// <remarks>
/// Both were wrong for the reported image failure. A faulty download was reported as a USB
/// preparation error and answered with "check the USB connection, or use another drive",
/// and "No USB data changes were applied." was the default for outcomes nobody had
/// established.
/// </remarks>
public sealed class ProvisioningFailureReportingTests
{
    private static readonly CreatorTransaction Transaction = new(CreatorStep.FormatUsb);

    private static UsbProvisioningResult Result(
        UsbProvisioningStatus status,
        UsbProvisioningFailure failure = UsbProvisioningFailure.Unspecified) =>
        new(Guid.NewGuid(), status, "message", Failure: failure);

    [Fact]
    public void AnImageFaultIsReportedAsAnImageErrorRatherThanAUsbPreparationError()
    {
        var result = UsbProvisionerClient.BuildProvisioningFailure(
            Result(UsbProvisioningStatus.FailedBeforeDiskChange, UsbProvisioningFailure.LiveImageBootAssets),
            "E:\\",
            Transaction);

        result.ErrorMessageKey.Should().Be("launcher.create.error.image");
    }

    [Fact]
    public void AnImageFaultIsNotAnsweredBySuggestingAnotherDrive()
    {
        var solution = CreatorActionService.GetCreateErrorSolutionKey("launcher.create.error.image");

        solution.Should().Be("launcher.action.create.solution.image");
        solution.Should().NotBe("launcher.action.create.solution");
    }

    [Theory]
    [InlineData(UsbProvisioningFailure.DeviceDisappeared, "launcher.create.error.device.disappeared")]
    [InlineData(UsbProvisioningFailure.DeviceReplaced, "launcher.create.error.device.replaced")]
    [InlineData(UsbProvisioningFailure.DeviceNotReady, "launcher.create.error.device.not_ready")]
    [InlineData(UsbProvisioningFailure.DeviceWriteProtected, "launcher.create.error.device.write_protected")]
    [InlineData(UsbProvisioningFailure.DeviceBusy, "launcher.create.error.device.busy")]
    public void EachDeviceFaultGetsItsOwnMessage(UsbProvisioningFailure failure, string expectedKey)
    {
        var result = UsbProvisionerClient.BuildProvisioningFailure(
            Result(UsbProvisioningStatus.InterruptedAfterDiskChange, failure),
            "E:\\",
            Transaction);

        result.ErrorMessageKey.Should().Be(expectedKey);
    }

    [Theory]
    [InlineData(UsbProvisioningFailure.DeviceDisappeared, "launcher.action.create.solution.device.disappeared")]
    [InlineData(UsbProvisioningFailure.DeviceReplaced, "launcher.action.create.solution.device.replaced")]
    [InlineData(UsbProvisioningFailure.DeviceNotReady, "launcher.action.create.solution.device.not_ready")]
    [InlineData(UsbProvisioningFailure.DeviceWriteProtected, "launcher.action.create.solution.device.write_protected")]
    [InlineData(UsbProvisioningFailure.DeviceBusy, "launcher.action.create.solution.device.busy")]
    public void EachDeviceFaultGetsItsOwnNextStep(UsbProvisioningFailure failure, string expectedSolution)
    {
        var errorKey = UsbProvisionerClient.ErrorMessageKeyFor(failure);

        CreatorActionService.GetCreateErrorSolutionKey(errorKey).Should().Be(expectedSolution);
    }

    [Fact]
    public void EveryDeviceFaultIsTranslatedInEveryFullDictionary()
    {
        // A classified fault the operator cannot read is no better than a raw Windows
        // sentence, so the message and its next step must exist in all three languages.
        var kinds = new[]
        {
            UsbProvisioningFailure.DeviceDisappeared,
            UsbProvisioningFailure.DeviceReplaced,
            UsbProvisioningFailure.DeviceNotReady,
            UsbProvisioningFailure.DeviceWriteProtected,
            UsbProvisioningFailure.DeviceBusy
        };

        foreach (var language in new[] { "en", "ru", "uk" })
        {
            var localizer = new Localizer(language);
            foreach (var kind in kinds)
            {
                var errorKey = UsbProvisionerClient.ErrorMessageKeyFor(kind);
                var solutionKey = CreatorActionService.GetCreateErrorSolutionKey(errorKey);

                localizer.T(errorKey).Should().NotBe(errorKey, "{0} must be translated in {1}", errorKey, language);
                localizer.T(solutionKey).Should().NotBe(solutionKey, "{0} must be translated in {1}", solutionKey, language);
            }

            localizer.T("launcher.wizard.create.provisioner_waitingfordevice")
                .Should().NotBe("launcher.wizard.create.provisioner_waitingfordevice");
        }
    }

    [Fact]
    public void TheWaitingStageHasTheProgressMessageTheProvisionerWillAskFor()
    {
        // The stage name is turned into a key by lower-casing it; a mismatch here would
        // show the operator a raw key while DexTor waits for their stick.
        UsbProvisionerClient.StageMessageKey(UsbProvisioningStage.WaitingForDevice)
            .Should().Be("launcher.wizard.create.provisioner_waitingfordevice");
    }

    [Fact]
    public void WaitingForTheDeviceStaysOnTheStepThatWasRunning()
    {
        // Not "Complete", which is where an unlisted stage would have fallen.
        UsbProvisionerClient.MapStage(UsbProvisioningStage.WaitingForDevice)
            .Should().Be(CreatorStep.FormatUsb);
    }

    [Fact]
    public void AnOrdinaryProvisioningFaultKeepsItsExistingErrorKey()
    {
        var result = UsbProvisionerClient.BuildProvisioningFailure(
            Result(UsbProvisioningStatus.FailedBeforeDiskChange),
            "E:\\",
            Transaction);

        result.ErrorMessageKey.Should().Be("launcher.create.error.provisioning");
    }

    [Theory]
    [InlineData(UsbProvisioningStatus.FailedBeforeDiskChange, "launcher.create.error.safety.unchanged")]
    [InlineData(UsbProvisioningStatus.Rejected, "launcher.create.error.safety.unchanged")]
    [InlineData(UsbProvisioningStatus.InterruptedAfterDiskChange, "launcher.create.error.safety.possibly_modified")]
    public void TheSafetyStatementFollowsWhatTheProvisionerEstablished(
        UsbProvisioningStatus status,
        string expectedKey)
    {
        var result = UsbProvisionerClient.BuildProvisioningFailure(Result(status), "E:\\", Transaction);

        result.ErrorSafetyMessageKey.Should().Be(expectedKey);
    }

    [Fact]
    public void AResultThatCouldNotBeReadClaimsNothingAboutTheDrive()
    {
        // The provisioner ran. Its outcome is unknown, which is not the same as "nothing
        // was written".
        var result = UsbProvisionerClient.BuildProvisioningFailure(null, "E:\\", Transaction);

        result.ErrorSafetyMessageKey.Should().Be("launcher.create.error.safety.unknown");
    }

    [Theory]
    [InlineData(null, "launcher.create.error.safety.unknown")]
    [InlineData(UsbProvisioningStage.Validating, "launcher.create.error.safety.unknown")]
    [InlineData(UsbProvisioningStage.CleaningDisk, "launcher.create.error.safety.possibly_modified")]
    [InlineData(UsbProvisioningStage.CopyingWindowsImage, "launcher.create.error.safety.possibly_modified")]
    public void AProvisionerThatVanishedIsJudgedByHowFarItGot(
        UsbProvisioningStage? reachedStage,
        string expectedKey)
    {
        UsbProvisionerClient.SafetyKeyForLostOutcome(reachedStage).Should().Be(expectedKey);
    }

    [Fact]
    public void TheNewFailureKindSurvivesTheProvisionerResultRoundTrip()
    {
        var original = Result(UsbProvisioningStatus.FailedBeforeDiskChange, UsbProvisioningFailure.LiveImageBootAssets);

        var restored = UsbProvisioningSerializer.DeserializeResult(
            UsbProvisioningSerializer.SerializeResult(original));

        restored!.Failure.Should().Be(UsbProvisioningFailure.LiveImageBootAssets);
    }

    [Fact]
    public void AResultWrittenWithoutTheFailureKindStillReads()
    {
        // An older provisioner's result carries no such field. Produced by removing it
        // from what this build writes, so the rest of the payload stays exactly as the
        // real serializer emits it.
        var current = UsbProvisioningSerializer.SerializeResult(
            Result(UsbProvisioningStatus.FailedBeforeDiskChange));
        var start = current.IndexOf(",\"failure\":", StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var end = current.IndexOfAny([',', '}'], start + 1);
        var json = current.Remove(start, end - start);
        json.Should().NotContain("\"failure\":", "the failure kind is what an older result lacks");

        var restored = UsbProvisioningSerializer.DeserializeResult(json);

        restored!.Failure.Should().Be(UsbProvisioningFailure.Unspecified);
        restored.Message.Should().Be("message");
    }
}
