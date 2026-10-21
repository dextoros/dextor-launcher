using System.ComponentModel;

using DexTor.Contracts;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

/// <summary>
/// Which of the few actionable faults a raw Windows failure actually is.
/// </summary>
public sealed class UsbFailureClassifierTests
{
    private static PhysicalDiskSnapshot Disk(bool readOnly = false) =>
        new(2, "USBSTOR-CHOSEN-0001", "SER-0001", 32_000_000_000, "USB", "Chosen stick", false, false, readOnly, false);

    private static UsbDeviceProbe Present(bool readOnly = false) =>
        new(UsbDevicePresence.Present, Disk(readOnly), "present");

    private static UsbDeviceProbe Absent() => new(UsbDevicePresence.Absent, null, "absent");

    private static UsbDeviceProbe Replaced() => new(UsbDevicePresence.Replaced, Disk(), "replaced");

    /// <summary>An IOException the way Windows produces one, with the code in the HRESULT.</summary>
    private static IOException WindowsError(int code) => new("windows", unchecked((int)(0x80070000 | (uint)code)));

    [Fact]
    public void AVanishedDeviceOutweighsWhateverTheCallReported()
    {
        // "The device is not ready." from a stick that has since left the bus is a
        // disconnection, and the operator needs to hear that, not the call's wording.
        UsbFailureClassifier.Classify(Absent(), WindowsError(21))
            .Should().Be(UsbProvisioningFailure.DeviceDisappeared);
    }

    [Fact]
    public void ADeviceSwapIsItsOwnFaultAndIsNeverCalledADisconnection()
    {
        UsbFailureClassifier.Classify(Replaced(), WindowsError(21))
            .Should().Be(UsbProvisioningFailure.DeviceReplaced);
    }

    [Theory]
    [InlineData(21, UsbProvisioningFailure.DeviceNotReady)]
    [InlineData(26, UsbProvisioningFailure.DeviceNotReady)]
    [InlineData(19, UsbProvisioningFailure.DeviceWriteProtected)]
    [InlineData(5, UsbProvisioningFailure.DeviceBusy)]
    [InlineData(32, UsbProvisioningFailure.DeviceBusy)]
    [InlineData(1167, UsbProvisioningFailure.DeviceDisappeared)]
    [InlineData(433, UsbProvisioningFailure.DeviceDisappeared)]
    public void APresentDeviceIsJudgedByTheWindowsErrorCode(int code, UsbProvisioningFailure expected)
    {
        UsbFailureClassifier.Classify(Present(), WindowsError(code)).Should().Be(expected);
    }

    [Fact]
    public void AReadOnlyDeviceIsWriteProtectedWhateverTheCallSaid()
    {
        UsbFailureClassifier.Classify(Present(readOnly: true), WindowsError(5))
            .Should().Be(UsbProvisioningFailure.DeviceWriteProtected);
    }

    [Fact]
    public void AnUnrecognizedFailureIsLeftUnclassifiedRatherThanGuessedAt()
    {
        // A wrong instruction costs the operator more than no instruction.
        UsbFailureClassifier.Classify(Present(), new InvalidOperationException("something else"))
            .Should().Be(UsbProvisioningFailure.Unspecified);
    }

    [Fact]
    public void AWin32ExceptionCarriesItsCodeToo()
    {
        UsbFailureClassifier.Classify(Present(), new Win32Exception(19))
            .Should().Be(UsbProvisioningFailure.DeviceWriteProtected);
    }

    [Fact]
    public void AnInnerWindowsErrorIsFound()
    {
        var wrapped = new IOException("outer", WindowsError(21));

        UsbFailureClassifier.Classify(Present(), wrapped).Should().Be(UsbProvisioningFailure.DeviceNotReady);
    }

    [Theory]
    [InlineData("The media is write protected.", UsbProvisioningFailure.DeviceWriteProtected)]
    [InlineData("The device is not ready.", UsbProvisioningFailure.DeviceNotReady)]
    [InlineData("Access is denied.", UsbProvisioningFailure.DeviceBusy)]
    [InlineData("The disk is in use.", UsbProvisioningFailure.DeviceBusy)]
    [InlineData("DiskPart has encountered an error.", UsbProvisioningFailure.Unspecified)]
    public void DiskPartOutputIsReadOnlyForThePhrasesItIsDocumentedToPrint(
        string output,
        UsbProvisioningFailure expected)
    {
        UsbFailureClassifier.ClassifyDiskPart(Present(), output).Should().Be(expected);
    }

    [Fact]
    public void DiskPartOutputNeverOverridesAVanishedDevice()
    {
        UsbFailureClassifier.ClassifyDiskPart(Absent(), "The media is write protected.")
            .Should().Be(UsbProvisioningFailure.DeviceDisappeared);
    }

    [Fact]
    public void AnUnclassifiedFailureKeepsTheCallersOwnWording()
    {
        UsbFailureClassifier.Describe(UsbProvisioningFailure.Unspecified, "original message")
            .Should().Be("original message");
    }

    [Fact]
    public void AClassifiedFailureReplacesTheRawWindowsSentence()
    {
        UsbFailureClassifier.Describe(UsbProvisioningFailure.DeviceDisappeared, "The device is not ready.")
            .Should().Contain("left the bus");
    }
}
