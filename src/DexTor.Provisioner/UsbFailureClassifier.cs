using System.Runtime.InteropServices;

using DexTor.Contracts;

namespace DexTor.Provisioner;

/// <summary>
/// Turns "something went wrong with the disk" into one of the few things an operator can
/// actually act on.
/// </summary>
/// <remarks>
/// Two sources are combined, and the order matters. The device's presence is asked for
/// live and decides first, because a Windows error code describes the moment the call
/// failed while the probe describes the situation the operator is now in: an
/// <c>ERROR_NOT_READY</c> from a stick that has since left the bus is a disappearance, not
/// a stalled device. Only when the device is still there does the error code choose
/// between not-ready, write-protected and busy.
/// <para>
/// Anything that cannot be placed stays <see cref="UsbProvisioningFailure.Unspecified"/>
/// rather than being forced into a category, because a wrong instruction — "try another
/// port" for a write-protected stick — wastes more of the operator's time than none.
/// </para>
/// </remarks>
public static class UsbFailureClassifier
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorNotReady = 21;
    private const int ErrorWriteProtect = 19;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorDeviceNotExist = 55;
    private const int ErrorUnrecognizedMedia = 26;
    private const int ErrorNoSuchDevice = 433;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorMediaChanged = 1110;
    private const int ErrorDeviceRemoved = 1617;

    /// <summary>Classifies a failure, given what the device is doing right now.</summary>
    public static UsbProvisioningFailure Classify(UsbDeviceProbe probe, Exception? exception = null)
    {
        ArgumentNullException.ThrowIfNull(probe);

        switch (probe.Presence)
        {
            case UsbDevicePresence.Absent:
                return UsbProvisioningFailure.DeviceDisappeared;
            case UsbDevicePresence.Replaced:
                return UsbProvisioningFailure.DeviceReplaced;
            default:
                break;
        }

        if (probe.Disk?.IsReadOnly == true)
        {
            return UsbProvisioningFailure.DeviceWriteProtected;
        }

        if (exception is null)
        {
            return UsbProvisioningFailure.Unspecified;
        }

        var code = WindowsErrorCode(exception);
        var byCode = FromWin32(code);
        // The stick is there and answering, yet a write was refused with a device error:
        // it stopped accepting data. Busy, not-ready and write-protected keep their own
        // meaning; this is everything a present device says when the media itself fails.
        if (byCode == UsbProvisioningFailure.Unspecified
            && (FindDiskWrite(exception) is not null || DeviceFaultCodes.Contains(code)))
        {
            return UsbProvisioningFailure.DeviceFaulty;
        }

        return byCode;
    }

    /// <summary>
    /// Device errors on a present stick: incorrect function, CRC, sector not found, write
    /// and read faults, general failure, I/O device error.
    /// </summary>
    private static readonly int[] DeviceFaultCodes = [1, 23, 27, 29, 30, 31, 1117];

    internal static DiskWriteException? FindDiskWrite(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is DiskWriteException write) return write;
        }

        return null;
    }

    /// <summary>
    /// Classifies a diskpart step that reported failure, given the same live probe.
    /// </summary>
    /// <remarks>
    /// diskpart reports through text, not error codes, so only the few phrases it is
    /// documented to print are read. Everything else stays unclassified rather than being
    /// guessed at from a localized console message.
    /// </remarks>
    public static UsbProvisioningFailure ClassifyDiskPart(UsbDeviceProbe probe, string? output)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var byPresence = Classify(probe);
        if (byPresence != UsbProvisioningFailure.Unspecified)
        {
            return byPresence;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            return UsbProvisioningFailure.Unspecified;
        }

        if (output.Contains("write protected", StringComparison.OrdinalIgnoreCase)
            || output.Contains("write-protected", StringComparison.OrdinalIgnoreCase))
        {
            return UsbProvisioningFailure.DeviceWriteProtected;
        }

        if (output.Contains("not ready", StringComparison.OrdinalIgnoreCase))
        {
            return UsbProvisioningFailure.DeviceNotReady;
        }

        if (output.Contains("in use", StringComparison.OrdinalIgnoreCase)
            || output.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return UsbProvisioningFailure.DeviceBusy;
        }

        return UsbProvisioningFailure.Unspecified;
    }

    /// <summary>
    /// The sentence that goes with a kind, for the result the provisioner returns.
    /// </summary>
    /// <remarks>
    /// The launcher localizes by kind and does not display this text; it is what lands in
    /// the provisioning journal, so it says what happened rather than what to do about it.
    /// </remarks>
    public static string Describe(UsbProvisioningFailure failure, string fallback) => failure switch
    {
        UsbProvisioningFailure.DeviceDisappeared =>
            "The selected USB left the bus during provisioning and did not come back.",
        UsbProvisioningFailure.DeviceReplaced =>
            "A different device now answers on the selected disk number; DexTor stopped instead of writing to it.",
        UsbProvisioningFailure.DeviceNotReady =>
            "The selected USB is present but Windows could not reach its media.",
        UsbProvisioningFailure.DeviceWriteProtected =>
            "The selected USB refuses writes.",
        UsbProvisioningFailure.DeviceBusy =>
            "The selected USB or its volume is held by another program on this computer.",
        UsbProvisioningFailure.DeviceFaulty =>
            "The selected USB is present but stopped accepting data. " + fallback,
        _ => fallback
    };

    private static UsbProvisioningFailure FromWin32(int code) => code switch
    {
        ErrorNotReady or ErrorUnrecognizedMedia or ErrorMediaChanged => UsbProvisioningFailure.DeviceNotReady,
        ErrorWriteProtect => UsbProvisioningFailure.DeviceWriteProtected,
        ErrorAccessDenied or ErrorSharingViolation or ErrorLockViolation => UsbProvisioningFailure.DeviceBusy,
        ErrorDeviceNotExist or ErrorNoSuchDevice or ErrorDeviceNotConnected or ErrorDeviceRemoved
            => UsbProvisioningFailure.DeviceDisappeared,
        _ => UsbProvisioningFailure.Unspecified
    };

    /// <summary>The Win32 code behind an exception, or zero when there is none.</summary>
    internal static int WindowsErrorCode(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            // Win32Exception keeps the native code separately; its ErrorCode is an
            // HRESULT that is usually just E_FAIL and says nothing about the device.
            if (current is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode != 0)
            {
                return win32.NativeErrorCode;
            }

            // IOException carries the Win32 code in the low word of a 0x8007xxxx HRESULT.
            // The mask is written as an int: an unsigned literal would promote the whole
            // comparison to long and never match a negative HRESULT.
            var hresult = current.HResult;
            if ((hresult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000))
            {
                return hresult & 0xFFFF;
            }
        }

        return 0;
    }
}
