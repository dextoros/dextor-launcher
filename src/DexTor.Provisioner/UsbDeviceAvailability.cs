using DexTor.Contracts;

namespace DexTor.Provisioner;

/// <summary>What the selected disk number holds right now.</summary>
public enum UsbDevicePresence
{
    /// <summary>The same physical device is there, by unique id and serial.</summary>
    Present,

    /// <summary>Nothing answers on that disk number.</summary>
    Absent,

    /// <summary>
    /// Something answers, but it is not the device the operator chose.
    /// </summary>
    /// <remarks>
    /// The dangerous case. Windows reuses disk numbers, so a stick that drops off and a
    /// different one that appears can land on the same number. Anything destructive has to
    /// stop here rather than continue against "disk 2".
    /// </remarks>
    Replaced
}

/// <summary>The device's presence, and the disk behind it when there is one.</summary>
public sealed record UsbDeviceProbe(UsbDevicePresence Presence, PhysicalDiskSnapshot? Disk, string Detail);

/// <summary>
/// Answers "is the operator's USB still the thing on that disk number?" without throwing,
/// and waits for it to come back when it briefly is not.
/// </summary>
/// <remarks>
/// A USB stick can leave the bus mid-operation — a marginal port, a hub, a controller that
/// re-enumerates after <c>clean</c> — and come back a few seconds later. Treating that as a
/// hard failure makes the operator repeat the whole run, which is what happened in manual
/// testing: two failed attempts and a third that worked with nothing changed.
/// <para>
/// Waiting is only ever for the <em>same</em> device. Identity is compared exactly the way
/// <see cref="UsbProvisioningPlanner.ValidateRequest"/> compares it, so a wait can never
/// end by accepting a different stick.
/// </para>
/// </remarks>
public static class UsbDeviceAvailability
{
    public static async Task<UsbDeviceProbe> ProbeAsync(
        IPhysicalDiskInventory inventory,
        UsbPhysicalIdentity expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(expected);

        PhysicalDiskSnapshot? disk;
        try
        {
            disk = await inventory.GetAsync(expected.DiskNumber, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The inventory itself could not answer. That is not evidence the device is
            // gone, and it is certainly not evidence it is still there.
            return new UsbDeviceProbe(
                UsbDevicePresence.Absent,
                null,
                $"The disk could not be queried: {exception.Message}");
        }

        if (disk is null)
        {
            return new UsbDeviceProbe(
                UsbDevicePresence.Absent,
                null,
                $"No disk answers on number {expected.DiskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        return Matches(expected, disk)
            ? new UsbDeviceProbe(UsbDevicePresence.Present, disk, "The selected device is present.")
            : new UsbDeviceProbe(
                UsbDevicePresence.Replaced,
                disk,
                $"Disk {disk.DiskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture)} is now a different device ({disk.FriendlyName}).");
    }

    /// <summary>
    /// Waits, up to <paramref name="timeout"/>, for the chosen device to be present again.
    /// </summary>
    /// <remarks>
    /// Returns as soon as the device is back, and stops early when a different device takes
    /// the number: waiting longer cannot turn that into the right device, and every extra
    /// second is a second in which the operator is told nothing useful.
    /// </remarks>
    public static async Task<UsbDeviceProbe> WaitForReturnAsync(
        IPhysicalDiskInventory inventory,
        UsbPhysicalIdentity expected,
        TimeSpan timeout,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? now = null,
        Action? onWaiting = null,
        CancellationToken cancellationToken = default)
    {
        delay ??= Task.Delay;
        now ??= () => DateTimeOffset.UtcNow;
        var deadline = now() + timeout;
        var notified = false;
        UsbDeviceProbe probe;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            probe = await ProbeAsync(inventory, expected, cancellationToken).ConfigureAwait(false);
            if (probe.Presence is UsbDevicePresence.Present or UsbDevicePresence.Replaced)
            {
                return probe;
            }

            if (now() >= deadline)
            {
                return probe;
            }

            if (!notified)
            {
                notified = true;
                onWaiting?.Invoke();
            }

            await delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>The same identity test the planner enforces, without the exception.</summary>
    private static bool Matches(UsbPhysicalIdentity expected, PhysicalDiskSnapshot actual) =>
        expected.DiskNumber == actual.DiskNumber
        && expected.CapacityBytes == actual.CapacityBytes
        && !string.IsNullOrWhiteSpace(expected.UniqueId)
        && !string.IsNullOrWhiteSpace(actual.UniqueId)
        && string.Equals(expected.UniqueId.Trim(), actual.UniqueId.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.SerialNumber.Trim(), actual.SerialNumber.Trim(), StringComparison.OrdinalIgnoreCase);
}
