using System;
using System.IO;

using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class UsbDiscoveryServiceTests
{
    [Fact]
    public void SpeedExcludesTheWarmUpChunkSoABurstDoesNotInflateIt()
    {
        var offsets = new List<long>();

        var (bytes, seconds) = UsbDiscoveryService.TimeChunks(
            16 * 1024 * 1024,
            4 * 1024 * 1024,
            TimeSpan.FromSeconds(30),
            offset =>
            {
                offsets.Add(offset);
                // The first chunk is slow (allocation, wake-up); the rest are steady.
                Thread.Sleep(offset == 0 ? 120 : 10);
            },
            CancellationToken.None);

        offsets.Should().Equal(0, 4L * 1024 * 1024, 8L * 1024 * 1024, 12L * 1024 * 1024);
        bytes.Should().Be(12L * 1024 * 1024, "only the three steady chunks are counted");
        seconds.Should().BeLessThan(0.1);
    }

    [Fact]
    public void SpeedProbeStopsAtItsTimeBudgetOnASlowDrive()
    {
        var chunks = 0;

        var (bytes, _) = UsbDiscoveryService.TimeChunks(
            64 * 1024 * 1024,
            4 * 1024 * 1024,
            TimeSpan.FromMilliseconds(60),
            _ =>
            {
                chunks++;
                Thread.Sleep(25);
            },
            CancellationToken.None);

        chunks.Should().BeLessThan(16);
        bytes.Should().BePositive();
    }

    [Fact]
    public async Task UnbufferedProbeMeasuresARealDirectoryAndCleansUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "dextor-speed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var result = await UsbDiscoveryService.MeasureAccessSpeedAsync(
                root, CancellationToken.None, 16 * 1024 * 1024, TimeSpan.FromSeconds(2));

            result.Should().NotBeNull();
            result!.WriteMegabytesPerSecond.Should().BePositive();
            result.ReadMegabytesPerSecond.Should().BePositive();
            Directory.EnumerateFileSystemEntries(root).Should().BeEmpty("the probe file is removed");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void UsbSpeedRatingMarksLowWritePerformanceAsSlow()
    {
        new UsbAccessSpeedResult(14.9, 100).IsSlow.Should().BeTrue();
        new UsbAccessSpeedResult(15, 20).IsSlow.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "", true)]
    [InlineData(true, "", true)]
    [InlineData(true, "exFAT", false)]
    public void SnapshotIdentifiesDrivesThatNeedAutomaticLayoutPreparation(
        bool isReady,
        string fileSystem,
        bool expected)
    {
        var snapshot = new UsbDriveSnapshot(
            @"E:\",
            string.Empty,
            fileSystem,
            null,
            null,
            isReady,
            isReady);

        snapshot.NeedsFormatting.Should().Be(expected);
    }

    [Fact]
    public void PhysicalDiscoveryIncludesRawUsbAndFiltersWindowsSystemDisk()
    {
        const string json = """
            [
              {
                "DiskNumber": 7,
                "UniqueId": "usb-7",
                "SerialNumber": "serial-7",
                "CapacityBytes": 68719476736,
                "BusType": "USB",
                "FriendlyName": "Raw USB",
                "IsBoot": false,
                "IsSystem": false,
                "IsReadOnly": false,
                "RootPath": "",
                "VolumeLabel": "",
                "FileSystem": "",
                "FreeBytes": null
              },
              {
                "DiskNumber": 0,
                "UniqueId": "system",
                "CapacityBytes": 1000000000000,
                "BusType": "USB",
                "FriendlyName": "Protected",
                "IsBoot": true,
                "IsSystem": true,
                "IsReadOnly": false
              }
            ]
            """;

        var snapshots = UsbDiscoveryService.ParsePhysicalUsbJson(json);

        snapshots.Should().ContainSingle();
        snapshots[0].NeedsFormatting.Should().BeTrue();
        snapshots[0].CanProvision.Should().BeTrue();
        snapshots[0].PhysicalIdentity!.DiskNumber.Should().Be(7);
        snapshots[0].PhysicalIdentity!.FriendlyName.Should().Be("Raw USB");
    }

    [Fact]
    public void AUsbHardDiskIsToldApartFromAStickByItsMediaType()
    {
        const string json = """
            [
              { "DiskNumber": 4, "UniqueId": "hdd", "CapacityBytes": 2000398934016, "BusType": "USB", "FriendlyName": "WD Elements",
                "IsBoot": false, "IsSystem": false, "IsReadOnly": false, "RootPath": "E:\\", "VolumeLabel": "WD", "FileSystem": "NTFS",
                "FreeBytes": 1300000000000, "MediaType": "External hard disk media" },
              { "DiskNumber": 5, "UniqueId": "stick", "CapacityBytes": 62062000000, "BusType": "USB", "FriendlyName": "Kingston",
                "IsBoot": false, "IsSystem": false, "IsReadOnly": false, "RootPath": "F:\\", "VolumeLabel": "", "FileSystem": "exFAT",
                "FreeBytes": 61955000000, "MediaType": "Removable Media" }
            ]
            """;

        var snapshots = UsbDiscoveryService.ParsePhysicalUsbJson(json);

        snapshots.Should().HaveCount(2);
        snapshots[0].IsExternalDisk.Should().BeTrue();
        snapshots[1].IsExternalDisk.Should().BeFalse();
        snapshots[0].CanProvision.Should().BeTrue("an external disk can be used, after an explicit confirmation");
    }

    [Fact]
    public void DetectsDextorUsbAmongCandidatePaths()
    {
        var candidateOne = CreateDrivePath();
        var candidateTwo = CreateDrivePath();
        var dextorDrive = Path.Combine(candidateOne, "DEXTOR", "metadata");
        Directory.CreateDirectory(dextorDrive);
        File.WriteAllText(Path.Combine(dextorDrive, "device.json"), "{}");

        try
        {
            UsbDiscoveryService.DetectDextorUsb([candidateOne, candidateTwo]).Should().Be(
                candidateOne.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(candidateOne, recursive: true);
            Directory.Delete(candidateTwo, recursive: true);
        }
    }

    [Fact]
    public void BuildCreateCandidatesExcludesDetectedDextorDrive()
    {
        var candidateOne = CreateDrivePath();
        var candidateTwo = CreateDrivePath();
        var candidates = new[] { candidateOne, candidateTwo };
        var createCandidates = UsbDiscoveryService.BuildCreateCandidates(candidates, candidateTwo);

        createCandidates.Should().HaveCount(1);
        createCandidates[0].Should().Be(candidateOne.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Directory.Delete(candidateOne, true);
        Directory.Delete(candidateTwo, true);
    }

    [Fact]
    public void BuildCreateCandidatesNormalizesAndDeduplicatesPaths()
    {
        var candidateOne = CreateDrivePath();
        var candidateDuplicate = candidateOne.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidateUpper = candidateOne.ToUpperInvariant();
        var createCandidates = UsbDiscoveryService.BuildCreateCandidates(
            [candidateOne, candidateDuplicate, candidateUpper],
            dextorRootPath: null);

        createCandidates.Should().HaveCount(1);
        createCandidates[0].Should().Be(candidateDuplicate);

        Directory.Delete(candidateOne, true);
    }

    [Fact]
    public void BuildCreateCandidatesSortsCandidatesAlphabetically()
    {
        var candidateA = CreateDrivePathWithName("dextor_usb_a");
        var candidateB = CreateDrivePathWithName("dextor_usb_b");

        try
        {
            var candidates = new[] { candidateB, candidateA };
            var createCandidates = UsbDiscoveryService.BuildCreateCandidates(candidates, dextorRootPath: null);

            createCandidates.Should().HaveCount(2);
            createCandidates[0].Should().Be(candidateA.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            createCandidates[1].Should().Be(candidateB.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(candidateA, true);
            Directory.Delete(candidateB, true);
        }
    }

    [Fact]
    public void BuildCreateCandidatesSkipsMissingCandidates()
    {
        var missingCandidate = Path.Combine(Path.GetTempPath(), "dextor_usb", Guid.NewGuid().ToString("N"));
        var existingCandidate = CreateDrivePath();

        try
        {
            var createCandidates = UsbDiscoveryService.BuildCreateCandidates(
                [missingCandidate, existingCandidate],
                dextorRootPath: null);

            createCandidates.Should().HaveCount(1);
            createCandidates[0].Should().Be(existingCandidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        finally
        {
            Directory.Delete(existingCandidate, true);
        }
    }

    [Fact]
    public void BuildCreateCandidatesExcludesCaseInsensitivePath()
    {
        var candidateOne = CreateDrivePath();
        var candidates = new[]
        {
            candidateOne,
            candidateOne.ToUpperInvariant()
        };
        var normalizedCandidate = candidateOne.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var createCandidates = UsbDiscoveryService.BuildCreateCandidates(
            candidates,
            normalizedCandidate.ToUpperInvariant());

        createCandidates.Should().BeEmpty();
        Directory.Delete(candidateOne, true);
    }

    [Fact]
    public void IsDexTorDrivePathMatchesMetadataOrLauncherFile()
    {
        var root = CreateDrivePath();
        var metaFile = Path.Combine(root, "DEXTOR", "metadata", "device.json");
        Directory.CreateDirectory(Path.GetDirectoryName(metaFile)!);
        File.WriteAllText(metaFile, "{}");

        try
        {
            UsbDiscoveryService.IsDexTorDrivePath(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void IsDexTorDrivePathMatchesInterruptedSetupTransaction()
    {
        var root = CreateDrivePath();
        var transactionPath = Path.Combine(root, "DEXTOR", "metadata", "creator-transaction.json");
        Directory.CreateDirectory(Path.GetDirectoryName(transactionPath)!);
        File.WriteAllText(transactionPath, "{}");

        try
        {
            UsbDiscoveryService.IsDexTorDrivePath(root).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string CreateDrivePath()
    {
        var path = Path.Combine(Path.GetTempPath(), "dextor_usb", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }

    private static string CreateDrivePathWithName(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;
    }
}
