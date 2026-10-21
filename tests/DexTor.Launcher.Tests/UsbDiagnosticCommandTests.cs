using System.Globalization;
using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// The runnable diagnostic, exercised against recorded topologies and temporary host files.
/// </summary>
/// <remarks>
/// No physical device is touched and none is selected here. The "device" is an ordinary
/// temporary directory whose paths the fixture topology answers for, which is what lets
/// the report destination guard — the one rule that must never be got wrong — be tested at
/// all: it compares volume identity, and a fixture can hand out two distinct volumes.
/// </remarks>
public sealed class UsbDiagnosticCommandTests : IDisposable
{
    private const string HostVolume = @"\\?\Volume{bbbbbbbb-5555-6666-7777-888888888888}\";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-diag-" + Guid.NewGuid().ToString("N"));
    private readonly string _deviceDirectory;
    private readonly string _hostDirectory;
    private readonly string _image;
    private readonly StringWriter _console = new();

    public UsbDiagnosticCommandTests()
    {
        _deviceDirectory = Path.Combine(_root, "device", "DEXTOR", "storage");
        _hostDirectory = Path.Combine(_root, "host");
        Directory.CreateDirectory(_deviceDirectory);
        Directory.CreateDirectory(_hostDirectory);

        _image = Path.Combine(_deviceDirectory, "data.img");
        using var stream = new FileStream(_image, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.SetLength(4L * 1024 * 1024);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temporary directory that outlives the test is not a test failure.
        }
    }

    /// <summary>An ordinary stick, with the host's own volume distinguishable from it.</summary>
    private FixtureTopology Topology()
    {
        var topology = FixtureTopology.OrdinaryStick();
        topology.VolumesByPathPrefix[_hostDirectory] = HostVolume;
        return topology;
    }

    private string Report(string name = "report.txt") => Path.Combine(_hostDirectory, name);

    private int Run(FixtureTopology topology, params string[] arguments) =>
        UsbDiagnosticCommand.Run(arguments, _console, topology);

    [Fact]
    public void ItWritesWhatTheResolverDecidedAndWhyToTheHostFile()
    {
        var report = Report();

        var code = Run(Topology(), "--data-image", _image, "--output", report);

        code.Should().Be(UsbDiagnosticExitCode.Accepted);
        var content = File.ReadAllText(report);
        content.Should().Contain("Build commit");
        content.Should().Contain("Session context");
        content.Should().Contain("Storage identity");
        content.Should().Contain("Device mapping");
        content.Should().Contain("Proposed removal target");
        content.Should().Contain(@"USB\VID_1234&PID_5678\0123456789");
        content.Should().Contain("Result           : accepted");
        content.Should().Contain("Exit code        : 0");
        content.Should().Contain(_image);
    }

    [Fact]
    public void ItNeverClaimsWindowsWillApproveTheRemoval()
    {
        var report = Report();

        Run(Topology(), "--data-image", _image, "--output", report);

        var content = File.ReadAllText(report);
        content.Should().Contain("not a promise that Windows will approve the removal");
        content.Should().Contain("says nothing about whether guest storage is consistent");
    }

    [Fact]
    public void ItRefusesToWriteTheReportOntoTheVolumeItIsInspecting()
    {
        var onTheDevice = Path.Combine(_deviceDirectory, "report.txt");

        var code = Run(Topology(), "--data-image", _image, "--output", onTheDevice);

        code.Should().Be(UsbDiagnosticExitCode.OutputRejected);
        File.Exists(onTheDevice).Should().BeFalse();
        _console.ToString().Should().Contain("volume being inspected");
    }

    [Fact]
    public void ItRefusesAnAliasOfTheInspectedVolumeAsWell()
    {
        // A different spelling — a mounted folder, another drive letter — of the same
        // volume. Identity is what is compared, not the path.
        var alias = Path.Combine(_root, "alias");
        Directory.CreateDirectory(alias);
        var topology = Topology();
        topology.VolumesByPathPrefix[alias] = FixtureTopology.VolumeGuid;

        var code = Run(topology, "--data-image", _image, "--output", Path.Combine(alias, "report.txt"));

        code.Should().Be(UsbDiagnosticExitCode.OutputRejected);
        Directory.GetFiles(alias).Should().BeEmpty();
    }

    [Fact]
    public void ItRefusesADestinationWhoseVolumeWindowsCannotName()
    {
        var topology = Topology();
        topology.VolumesByPathPrefix.Clear();
        topology.VolumeFailure = "The path is not on a resolvable local volume.";

        var code = Run(topology, "--data-image", _image, "--output", Report());

        code.Should().Be(UsbDiagnosticExitCode.OutputRejected);
        File.Exists(Report()).Should().BeFalse();
    }

    [Fact]
    public void ItWillNotCreateTheReportDirectory()
    {
        var absent = Path.Combine(_hostDirectory, "not-there");

        var code = Run(Topology(), "--data-image", _image, "--output", Path.Combine(absent, "report.txt"));

        code.Should().Be(UsbDiagnosticExitCode.OutputRejected);
        Directory.Exists(absent).Should().BeFalse();
    }

    [Fact]
    public void ItLeavesTheInspectedDeviceExactlyAsItFoundIt()
    {
        var before = Snapshot(Path.Combine(_root, "device"));

        Run(Topology(), "--data-image", _image, "--output", Report());

        Snapshot(Path.Combine(_root, "device")).Should().Equal(before);
    }

    [Fact]
    public void ItOpensTheImageWithoutAskingForWriteAccess()
    {
        // The holder denies write sharing. A diagnostic that asked for write access would
        // fail here; one that asks only to read does not.
        using var holder = new FileStream(_image, FileMode.Open, FileAccess.Read, FileShare.Read);

        var code = Run(Topology(), "--data-image", _image, "--output", Report());

        code.Should().Be(UsbDiagnosticExitCode.Accepted);
        File.ReadAllText(Report()).Should().Contain("Readable         : yes");
    }

    [Fact]
    public void AnImageThatIsNotThereIsReportedAsMissingRatherThanAsARefusal()
    {
        var code = Run(
            Topology(),
            "--data-image",
            Path.Combine(_deviceDirectory, "gone.img"),
            "--output",
            Report());

        code.Should().Be(UsbDiagnosticExitCode.TargetMissing);
        File.ReadAllText(Report()).Should().Contain("Result           : target missing");
    }

    [Fact]
    public void StorageThatCannotBeReadIsReportedAsUnknownRatherThanAsARefusal()
    {
        var topology = Topology();
        topology.VolumeFailure = "The path is not on a resolvable local volume.";

        var code = Run(topology, "--data-image", _image, "--output", Report());

        code.Should().Be(UsbDiagnosticExitCode.TargetUnknown);
        var content = File.ReadAllText(Report());
        content.Should().Contain("Volume           : unresolved");
        content.Should().Contain("unknown — the storage could not be read");
    }

    [Fact]
    public void ARefusalCarriesTheReasonTheResolverGave()
    {
        var topology = Topology();
        topology.InstanceIds[FixtureTopology.UsbDevice] = @"USB\VID_1234&PID_5678&MI_00\6&1a&0&0000";

        var code = Run(topology, "--data-image", _image, "--output", Report());

        code.Should().Be(UsbDiagnosticExitCode.Refused);
        var content = File.ReadAllText(Report());
        content.Should().Contain("Decision         : refused");
        content.Should().Contain("composite device");
        content.Should().Contain("Result           : refused");
    }

    [Fact]
    public void ADeviceNodeQueryThatFailsIsRefusedWithWhatWasUnknownNamed()
    {
        // Deeper unknowns are the resolver's refusal, reported as one, with the failure in
        // the reason. Only the storage this command reads itself becomes exit code 13.
        var topology = Topology();
        topology.ChildrenFailure = "CONFIGRET 0x00000013";

        var code = Run(topology, "--data-image", _image, "--output", Report());

        code.Should().Be(UsbDiagnosticExitCode.Refused);
        File.ReadAllText(Report()).Should().Contain("CONFIGRET 0x00000013");
    }

    [Fact]
    public void ItWillNotRunWithoutAnExplicitlyChosenTarget()
    {
        var code = Run(Topology(), "--output", Report());

        code.Should().Be(UsbDiagnosticExitCode.Usage);
        _console.ToString().Should().Contain("will not pick a device");
        Directory.GetFiles(_hostDirectory).Should().BeEmpty();
    }

    [Fact]
    public void ItWillNotRunWithoutAnExplicitReportDestination()
    {
        var code = Run(Topology(), "--data-image", _image);

        code.Should().Be(UsbDiagnosticExitCode.Usage);
        _console.ToString().Should().Contain("--output is required");
    }

    [Theory]
    [InlineData("--data-image")]
    [InlineData("--output")]
    public void AnOptionWithoutItsValueIsAUsageErrorRatherThanAGuess(string option)
    {
        var code = Run(Topology(), option);

        code.Should().Be(UsbDiagnosticExitCode.Usage);
        _console.ToString().Should().Contain("needs a value");
    }

    [Fact]
    public void AnUnrecognizedArgumentStopsTheRun()
    {
        var code = Run(Topology(), "--data-image", _image, "--output", Report(), "--eject");

        code.Should().Be(UsbDiagnosticExitCode.Usage);
        _console.ToString().Should().Contain("Unrecognized argument: --eject");
        File.Exists(Report()).Should().BeFalse();
    }

    [Fact]
    public void PathsWithSpacesAndNonAsciiCharactersArriveWhole()
    {
        var awkwardDevice = Path.Combine(_root, "съёмный диск (test)", "DEXTOR", "storage");
        var awkwardHost = Path.Combine(_root, "отчёты и логи");
        Directory.CreateDirectory(awkwardDevice);
        Directory.CreateDirectory(awkwardHost);
        var image = Path.Combine(awkwardDevice, "data image.img");
        File.WriteAllBytes(image, new byte[64]);
        var report = Path.Combine(awkwardHost, "usb report.txt");

        var topology = FixtureTopology.OrdinaryStick();
        topology.VolumesByPathPrefix[awkwardHost] = HostVolume;

        var code = UsbDiagnosticCommand.Run(
            ["--data-image", image, "--output", report],
            _console,
            topology);

        code.Should().Be(UsbDiagnosticExitCode.Accepted);
        File.ReadAllText(report).Should().Contain(image);
    }

    [Fact]
    public void AnExistingReportIsKeptUnlessOverwritingIsAskedForExplicitly()
    {
        var report = Report();
        File.WriteAllText(report, "an earlier report");

        var code = Run(Topology(), "--data-image", _image, "--output", report);

        code.Should().Be(UsbDiagnosticExitCode.OutputRejected);
        File.ReadAllText(report).Should().Be("an earlier report");

        var forced = Run(Topology(), "--data-image", _image, "--output", report, "--force");

        forced.Should().Be(UsbDiagnosticExitCode.Accepted);
        File.ReadAllText(report).Should().Contain("Result           : accepted");
    }

    [Fact]
    public void TheReportNamesTheBuildThatMadeTheDecision()
    {
        Run(Topology(), "--data-image", _image, "--output", Report());

        // Either a commit, or a plain statement that this build carries no stamp. Never
        // silence, and never an invented value.
        File.ReadAllText(Report()).Should().MatchRegex(@"Build commit     : (\w{40}|unknown)");
    }

    private static string[] Snapshot(string directory) =>
        [.. Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(entry => File.Exists(entry)
                ? string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entry}|{new FileInfo(entry).Length}|{File.GetLastWriteTimeUtc(entry):O}")
                : entry)];
}
