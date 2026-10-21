using DexTor.Contracts;

using FluentAssertions;

using Xunit;

namespace DexTor.Provisioner.Tests;

public sealed class DataPartitionFormatTests
{
    private static readonly UsbPhysicalIdentity Chosen = new(7, "unique-usb-id", "serial-123", 64L * 1024 * 1024 * 1024, "USB", "Test USB", false, false, false);

    private static PhysicalDiskSnapshot Disk(string uniqueId = "unique-usb-id") =>
        new(7, uniqueId, "serial-123", 64L * 1024 * 1024 * 1024, "USB", "Test USB", false, false, false, false);

    private static UsbProvisioningRequest Request() =>
        new(Guid.NewGuid(), Chosen, @"C:\image\dextor.iso", new string('a', 64), 32L * 1024 * 1024 * 1024, true);

    private sealed class DiskPart(params bool[] outcomes) : IDiskPartExecutor
    {
        public int Calls { get; private set; }

        public Task<(bool Success, string Detail)> ExecuteAsync(string script, CancellationToken cancellationToken = default)
        {
            var success = outcomes[Math.Min(Calls, outcomes.Length - 1)];
            Calls++;
            return Task.FromResult((success, success
                ? "DiskPart successfully formatted the volume."
                : "Microsoft DiskPart version 10.0\r\nThere is no partition selected.\r\nDiskPart has encountered an error."));
        }
    }

    private sealed class Inventory(PhysicalDiskSnapshot? answer) : IPhysicalDiskInventory
    {
        public Task<PhysicalDiskSnapshot?> GetAsync(int diskNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(answer);
    }

    private sealed class NoWriter : IHybridDiskWriter
    {
        public Task<HybridGptLayout> WriteAsync(string imagePath, int diskNumber, long diskCapacityBytes, IProgress<long>? progress = null, bool verify = true, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Not used.");
    }

    [Fact]
    public async Task ATransientFailureRightAfterTheRawWriteIsRetriedOnTheSameDevice()
    {
        var diskPart = new DiskPart(false, false, true);
        var stages = new List<UsbProvisioningStage>();
        var delays = new List<TimeSpan>();
        var provisioner = new HybridUsbProvisioner(new Inventory(Disk()), diskPart, new NoWriter(), stages.Add);

        var result = await provisioner.FormatDataPartitionAsync(
            Request(),
            "script",
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        result.Success.Should().BeTrue();
        diskPart.Calls.Should().Be(3);
        delays.Should().Equal(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
        stages.Should().Contain(UsbProvisioningStage.WaitingForDataVolume);
    }

    [Fact]
    public async Task APersistentFailureStopsAfterTheLastAttemptAndKeepsEveryOutput()
    {
        var diskPart = new DiskPart(false);
        var provisioner = new HybridUsbProvisioner(new Inventory(Disk()), diskPart, new NoWriter());

        var result = await provisioner.FormatDataPartitionAsync(Request(), "script", (_, _) => Task.CompletedTask);

        result.Success.Should().BeFalse();
        diskPart.Calls.Should().Be(HybridUsbProvisioner.DataFormatAttempts);
        result.Detail.Should().Contain("attempt 1:").And.Contain($"attempt {HybridUsbProvisioner.DataFormatAttempts}:");
        result.Detail.Should().Contain("There is no partition selected.");
        result.Detail.Should().NotContain("Microsoft DiskPart version", "the banner says nothing about the fault");
    }

    [Fact]
    public async Task ASwappedDeviceIsNeverRetriedAgainst()
    {
        var diskPart = new DiskPart(false, true);
        var provisioner = new HybridUsbProvisioner(new Inventory(Disk("another-device")), diskPart, new NoWriter());

        var result = await provisioner.FormatDataPartitionAsync(Request(), "script", (_, _) => Task.CompletedTask);

        result.Success.Should().BeFalse();
        diskPart.Calls.Should().Be(1, "a different device now answers on that disk number");
    }

    [Fact]
    public void TheFormatScriptSelectsTheDiskAfterWindowsRescansTheNewTable()
    {
        var plan = UsbProvisioningPlanner.Build(Request(), Disk(), 'Z', 4);
        var script = plan.FormatDataPartitionScript;

        script.IndexOf("rescan", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("select disk 7", StringComparison.Ordinal));
        script.IndexOf("select disk 7", StringComparison.Ordinal)
            .Should().BeLessThan(script.IndexOf("select partition 4", StringComparison.Ordinal));
    }

    [Fact]
    public void TheServiceFolderIsHiddenSoTheDriveLooksOrdinary()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor-hide-" + Guid.NewGuid().ToString("N"));
        var service = Directory.CreateDirectory(Path.Combine(root, "DEXTOR", "metadata"));
        File.WriteAllText(Path.Combine(service.FullName, "device.json"), "{}");
        try
        {
            HybridUsbProvisioner.HideServiceDirectory(root);

            var attributes = File.GetAttributes(Path.Combine(root, "DEXTOR"));
            attributes.Should().HaveFlag(FileAttributes.Hidden);
            attributes.Should().HaveFlag(FileAttributes.System);
            File.Exists(Path.Combine(root, "DEXTOR", "metadata", "device.json"))
                .Should().BeTrue("hidden is not removed: the launcher still opens it by path");
        }
        finally
        {
            File.SetAttributes(Path.Combine(root, "DEXTOR"), FileAttributes.Directory);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void HidingIsSkippedQuietlyWhenThereIsNothingToHide()
    {
        var action = () => HybridUsbProvisioner.HideServiceDirectory(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid().ToString("N")));

        action.Should().NotThrow();
    }

    private sealed class Locator(params string?[] answers) : IDataVolumeLocator
    {
        public int Calls { get; private set; }
        public long? Offset { get; private set; }

        public Task<string?> FindAsync(int diskNumber, long partitionOffsetBytes, CancellationToken cancellationToken = default)
        {
            Offset = partitionOffsetBytes;
            return Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]);
        }
    }

    [Fact]
    public async Task TheDataVolumeIsFoundByItsPathWithoutEverNeedingALetter()
    {
        var locator = new Locator(null, null, @"\\?\Volume{0000-1111}\");
        var provisioner = new HybridUsbProvisioner(new Inventory(Disk()), new DiskPart(true), new NoWriter(), null, locator);

        var root = await provisioner.WaitForVolumePathAsync(7, 2048L * 512, (_, _) => Task.CompletedTask);

        root.Should().Be(@"\\?\Volume{0000-1111}\");
        locator.Calls.Should().Be(3);
        locator.Offset.Should().Be(2048L * 512);
    }

    [Fact]
    public async Task AVolumeThatNeverMountsEndsWithAnActionableError()
    {
        var provisioner = new HybridUsbProvisioner(new Inventory(Disk()), new DiskPart(true), new NoWriter(), null, new Locator((string?)null));

        var action = () => provisioner.WaitForVolumePathAsync(7, 1, (_, _) => Task.CompletedTask, maxAttempts: 3);

        await action.Should().ThrowAsync<IOException>().WithMessage("*finish mounting*");
    }

    [Theory]
    [InlineData("\\\\?\\Volume{abc}\\\r\n", "\\\\?\\Volume{abc}\\")]
    [InlineData("#< CLIXML\r\n\\\\?\\Volume{abc}\\\r\n<Objs Version=\"1.1.0.1\"></Objs>", "\\\\?\\Volume{abc}\\")]
    [InlineData("\\\\?\\Volume{abc}", "\\\\?\\Volume{abc}\\")]
    [InlineData("", null)]
    [InlineData("C:\\", null)]
    public void TheVolumePathIsPickedOutOfWhateverPowerShellPrints(string output, string? expected)
    {
        PowerShellDataVolumeLocator.ParseVolumePath(output).Should().Be(expected);
    }

    [Fact]
    public void PathsWrittenThroughTheVolumeAreReportedThroughTheLetter()
    {
        HybridUsbProvisioner.Rebase(@"\\?\Volume{abc}\DEXTOR\storage\data.img", @"\\?\Volume{abc}\", @"Y:\")
            .Should().Be(@"Y:\DEXTOR\storage\data.img");
    }

    [Fact]
    public void WithItsVolumesLockedTheStickIsNeverLeftWithoutAPartitionTable()
    {
        var plan = UsbProvisioningPlanner.Build(Request(), Disk(), 'Z', 4);

        var script = HybridUsbProvisioner.PrepareScriptFor(plan, volumesLocked: true);

        script.Should().NotContain("clean", "clean leaves a table-less disk that Windows offers to format");
        script.Should().NotContain("convert");
        script.Should().Contain("select disk 7").And.Contain("attributes disk clear readonly");
    }

    [Fact]
    public void OnlyAStickWhoseVolumesRefuseTheLockIsClearedTheOldWay()
    {
        var plan = UsbProvisioningPlanner.Build(Request(), Disk(), 'Z', 4);

        HybridUsbProvisioner.PrepareScriptFor(plan, volumesLocked: false)
            .Should().Contain("clean").And.Contain("convert gpt");
    }

    private sealed class RefusingFlushStream(string path, int hresult) : FileStream(path, FileMode.Create)
    {
        public int DeviceFlushes { get; private set; }

        public override void Flush(bool flushToDisk)
        {
            if (flushToDisk)
            {
                DeviceFlushes++;
                throw new IOException("Incorrect function.", hresult);
            }

            base.Flush(false);
        }
    }

    [Fact]
    public void AStickThatCannotFlushItsCacheDoesNotFailCreation()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor-flush-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new RefusingFlushStream(path, DurableFlush.ErrorInvalidFunctionHResult);
            stream.WriteByte(1);

            var action = () => DurableFlush.Flush(stream);

            action.Should().NotThrow("ERROR_INVALID_FUNCTION only means the device has no cache-flush command");
            stream.DeviceFlushes.Should().Be(1);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AnyOtherFlushFailureStillStopsCreation()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor-flush-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var stream = new RefusingFlushStream(path, unchecked((int)0x8007001F)); // ERROR_GEN_FAILURE

            var action = () => DurableFlush.Flush(stream);

            action.Should().Throw<IOException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AFailureIsJournalledWithItsTypeCodeAndWhereItCameFrom()
    {
        IOException caught;
        try
        {
            throw new IOException("Incorrect function.", DurableFlush.ErrorInvalidFunctionHResult);
        }
        catch (IOException exception)
        {
            caught = exception;
        }

        var detail = HybridUsbProvisioner.Describe(caught);

        detail.Should().Contain("System.IO.IOException").And.Contain("hresult=0x80070001").And.Contain("Incorrect function.");
        detail.Should().Contain(nameof(AFailureIsJournalledWithItsTypeCodeAndWhereItCameFrom), "the stack says which call failed");
    }

    private sealed class FailingDisk(long failAt) : MemoryStream(new byte[64 * 1024 * 1024], writable: true)
    {
        // A derived MemoryStream routes every write through this overload.
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position >= failAt) throw new IOException("Incorrect function.", DurableFlush.ErrorInvalidFunctionHResult);
            base.Write(buffer, offset, count);
        }
    }

    [Fact]
    public async Task AWriteTheStickRefusesSaysWhereItStopped()
    {
        await using var image = new MemoryStream(HybridGptDiskWriterTestsAccess.Image(32 * 1024));
        await using var disk = new FailingDisk(8L * 1024 * 1024);

        var action = () => HybridGptDiskWriter.WriteAsync(image, disk, disk.Length);

        var thrown = (await action.Should().ThrowAsync<DiskWriteException>()).Which;
        // The offset is where the refused chunk began: within one 4 MiB chunk of the fault.
        thrown.Offset.Should().BeInRange(4L * 1024 * 1024, 12L * 1024 * 1024);
        thrown.ErrorCode.Should().Be(1);
    }

    [Fact]
    public void ARefusedWriteOnAPresentStickIsAFaultNotAnUnknown()
    {
        var present = new UsbDeviceProbe(UsbDevicePresence.Present, Disk(), "present");
        var refused = new DiskWriteException(1_400_000_000, new IOException("Incorrect function.", DurableFlush.ErrorInvalidFunctionHResult));

        UsbFailureClassifier.Classify(present, refused).Should().Be(UsbProvisioningFailure.DeviceFaulty);
        UsbFailureClassifier.Classify(present, new IOException("CRC", unchecked((int)0x80070017))).Should().Be(UsbProvisioningFailure.DeviceFaulty);
        UsbFailureClassifier.Classify(new UsbDeviceProbe(UsbDevicePresence.Absent, null, "gone"), refused)
            .Should().Be(UsbProvisioningFailure.DeviceDisappeared, "a stick that left is not called faulty");
        UsbFailureClassifier.Classify(present, new IOException("busy", unchecked((int)0x80070020)))
            .Should().Be(UsbProvisioningFailure.DeviceBusy, "busy keeps its own meaning");
    }
    [Fact]
    public void TheJournalStaysInTheUsersOwnDexTorFolder()
    {
        HybridUsbProvisioner.JournalDirectory.Should().Be(DexTorStorage.Journal);
    }
}
