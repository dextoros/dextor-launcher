using DexTor.SessionHost;
using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

/// <summary>
/// The read-only diagnostic, exercised against recorded topologies.
/// </summary>
/// <remarks>
/// No physical device is touched here and none is auto-selected: the entry point requires
/// an explicitly named path, which is the whole point of it.
/// </remarks>
public sealed class UsbRemovalDiagnosticsTests
{
    private const string ImagePath = @"E:\DEXTOR\storage\data.img";

    [Fact]
    public void ItRefusesToRunWithoutAnExplicitlyChosenTarget()
    {
        // There is deliberately no "first removable disk" selection to fall back on.
        var act = () => UsbRemovalDiagnostics.Describe(string.Empty);

        act.Should().Throw<ArgumentException>().WithMessage("*will not pick a device*");
    }

    [Fact]
    public void ItReportsTheAcceptedTargetAndTheChainThatLedToIt()
    {
        var report = UsbRemovalDiagnostics.Describe(ImagePath, FixtureTopology.OrdinaryStick());

        report.Should().Contain("Decision         : accepted");
        report.Should().Contain(@"USB\VID_1234&PID_5678\0123456789");
        report.Should().Contain(@"\\?\Volume{aaaaaaaa-1111-2222-3333-444444444444}\");
        report.Should().Contain("Bus type");
        report.Should().Contain("System disk");
    }

    [Fact]
    public void ItReportsTheRejectionReasonWhenThereIsNoValidTarget()
    {
        var topology = FixtureTopology.OrdinaryStick();
        topology.InstanceIds[FixtureTopology.UsbDevice] = @"USB\VID_1234&PID_5678&MI_00\6&1a&0&0000";

        var report = UsbRemovalDiagnostics.Describe(ImagePath, topology);

        report.Should().Contain("Decision         : refused");
        report.Should().Contain("composite device");
    }

    [Fact]
    public void ItReportsNativeFailuresAsUnknownRatherThanAsAbsence()
    {
        var topology = FixtureTopology.OrdinaryStick();
        topology.ChildrenFailure = "CONFIGRET 0x00000013";

        var report = UsbRemovalDiagnostics.Describe(ImagePath, topology);

        report.Should().Contain("unknown");
        report.Should().Contain("Decision         : refused");
    }

    [Fact]
    public void ItSaysPlainlyThatItChangedNothing()
    {
        var report = UsbRemovalDiagnostics.Describe(ImagePath, FixtureTopology.OrdinaryStick());

        report.Should().Contain("No flush, eject, dismount, repair or write was performed.");
    }

    [Fact]
    public void ItRecordsTheSessionContextTheDecisionWasMadeIn()
    {
        var report = UsbRemovalDiagnostics.Describe(ImagePath, FixtureTopology.OrdinaryStick());

        // Privilege matters for what a later flush could do, so it is part of the picture.
        report.Should().Contain("Elevated");
        report.Should().Contain("Interactive");
    }
}
