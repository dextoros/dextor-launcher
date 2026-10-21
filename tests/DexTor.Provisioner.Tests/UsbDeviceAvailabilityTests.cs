using DexTor.Contracts;

using FluentAssertions;
using Xunit;

namespace DexTor.Provisioner.Tests;

/// <summary>
/// Presence is decided by identity, never by disk number.
/// </summary>
/// <remarks>
/// Windows reuses disk numbers. A stick that drops off during provisioning and a different
/// one that appears can both be "disk 2", so every answer here is about the device the
/// operator chose, not about the slot it occupied.
/// </remarks>
public sealed class UsbDeviceAvailabilityTests
{
    private static readonly UsbPhysicalIdentity Chosen = new(
        DiskNumber: 2,
        UniqueId: "USBSTOR-CHOSEN-0001",
        SerialNumber: "SER-0001",
        CapacityBytes: 32_000_000_000,
        BusType: "USB",
        FriendlyName: "Chosen stick",
        IsBoot: false,
        IsSystem: false,
        IsReadOnly: false);

    private static PhysicalDiskSnapshot Snapshot(
        string uniqueId = "USBSTOR-CHOSEN-0001",
        string serial = "SER-0001",
        long capacity = 32_000_000_000,
        bool readOnly = false) =>
        new(2, uniqueId, serial, capacity, "USB", "Chosen stick", false, false, readOnly, false);

    private sealed class Inventory(params PhysicalDiskSnapshot?[] answers) : IPhysicalDiskInventory
    {
        private int _index;

        public int Calls { get; private set; }

        public Task<PhysicalDiskSnapshot?> GetAsync(int diskNumber, CancellationToken cancellationToken = default)
        {
            Calls++;
            var answer = answers[Math.Min(_index, answers.Length - 1)];
            _index++;
            return Task.FromResult(answer);
        }
    }

    private sealed class ThrowingInventory : IPhysicalDiskInventory
    {
        public Task<PhysicalDiskSnapshot?> GetAsync(int diskNumber, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("PowerShell is unavailable.");
    }

    [Fact]
    public async Task TheChosenDeviceIsPresentWhenItsIdentityStillMatches()
    {
        var probe = await UsbDeviceAvailability.ProbeAsync(new Inventory(Snapshot()), Chosen);

        probe.Presence.Should().Be(UsbDevicePresence.Present);
    }

    [Fact]
    public async Task NothingOnTheDiskNumberIsAbsent()
    {
        var probe = await UsbDeviceAvailability.ProbeAsync(new Inventory([null]), Chosen);

        probe.Presence.Should().Be(UsbDevicePresence.Absent);
    }

    [Theory]
    [InlineData("USBSTOR-OTHER-0002", "SER-0001", 32_000_000_000L)]
    [InlineData("USBSTOR-CHOSEN-0001", "SER-9999", 32_000_000_000L)]
    [InlineData("USBSTOR-CHOSEN-0001", "SER-0001", 64_000_000_000L)]
    public async Task ADifferentDeviceOnTheSameNumberIsReplacedNotPresent(
        string uniqueId,
        string serial,
        long capacity)
    {
        var probe = await UsbDeviceAvailability.ProbeAsync(
            new Inventory(Snapshot(uniqueId, serial, capacity)),
            Chosen);

        probe.Presence.Should().Be(UsbDevicePresence.Replaced);
    }

    [Fact]
    public async Task ADeviceWithNoUniqueIdIsNeverTreatedAsTheChosenOne()
    {
        var probe = await UsbDeviceAvailability.ProbeAsync(new Inventory(Snapshot(uniqueId: " ")), Chosen);

        probe.Presence.Should().Be(UsbDevicePresence.Replaced);
    }

    [Fact]
    public async Task AnInventoryThatThrowsIsNotReadAsThePresenceOfAnything()
    {
        var probe = await UsbDeviceAvailability.ProbeAsync(new ThrowingInventory(), Chosen);

        probe.Presence.Should().Be(UsbDevicePresence.Absent);
        probe.Detail.Should().Contain("could not be queried");
    }

    [Fact]
    public async Task AStickThatComesBackEndsTheWait()
    {
        // The reported case: the device drops off mid-run and re-enumerates seconds later.
        var inventory = new Inventory(null, null, Snapshot());
        var waiting = 0;

        var probe = await UsbDeviceAvailability.WaitForReturnAsync(
            inventory,
            Chosen,
            TimeSpan.FromSeconds(20),
            delay: (_, _) => Task.CompletedTask,
            onWaiting: () => waiting++);

        probe.Presence.Should().Be(UsbDevicePresence.Present);
        waiting.Should().Be(1, "the operator is told once that DexTor is waiting, not on every poll");
    }

    [Fact]
    public async Task AStickThatNeverComesBackGivesUpAtTheDeadline()
    {
        var clock = DateTimeOffset.UnixEpoch;

        var probe = await UsbDeviceAvailability.WaitForReturnAsync(
            new Inventory([null]),
            Chosen,
            TimeSpan.FromSeconds(20),
            delay: (interval, _) => { clock += interval; return Task.CompletedTask; },
            now: () => clock);

        probe.Presence.Should().Be(UsbDevicePresence.Absent);
    }

    [Fact]
    public async Task ADifferentDeviceTakingTheNumberStopsTheWaitImmediately()
    {
        // Waiting longer cannot turn another stick into the chosen one, and continuing
        // would be continuing against someone else's disk.
        var inventory = new Inventory(null, Snapshot(uniqueId: "USBSTOR-OTHER-0002"));

        var probe = await UsbDeviceAvailability.WaitForReturnAsync(
            inventory,
            Chosen,
            TimeSpan.FromSeconds(20),
            delay: (_, _) => Task.CompletedTask);

        probe.Presence.Should().Be(UsbDevicePresence.Replaced);
        inventory.Calls.Should().Be(2);
    }
}
