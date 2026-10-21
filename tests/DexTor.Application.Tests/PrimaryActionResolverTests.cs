using FluentAssertions;
using Xunit;

using DexTor.Application;

namespace DexTor.Application.Tests;

public class PrimaryActionResolverTests
{
    [Fact]
    public void CreateUsbWhenUsbIsNormalAndReadyToUse()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = false
        });

        action.Should().Be(PrimaryAction.CreateDexTorUsb);
    }

    [Fact]
    public void StartDextorWhenSignedDextorUsbConnected()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true
        });

        action.Should().Be(PrimaryAction.StartDexTor);
    }

    [Fact]
    public void ContinueSetupWhenInterruptedSetupDetected()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasSetupInterrupted = true
        });

        action.Should().Be(PrimaryAction.ContinueSetup);
    }

    [Fact]
    public void UpdateRequiredReturnsUpdateAndStart()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasRequiredUpdate = true,
            IsSessionRunning = false
        });

        action.Should().Be(PrimaryAction.UpdateAndStart);
    }

    [Fact]
    public void OptionalUpdateReturnsRepairAndStart()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasOptionalUpdate = true,
            IsSessionRunning = false
        });

        action.Should().Be(PrimaryAction.RepairAndStart);
    }

    [Fact]
    public void ConnectUsbWhenInterruptedButNotConnected()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = false,
            IsDexTorUsb = false,
            HasSetupInterrupted = true
        });

        action.Should().Be(PrimaryAction.ConnectDexTorUsb);
    }

    [Fact]
    public void CheckAndRecoverHasPriorityOverContinueSetup()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasSetupInterrupted = true,
            HasUncleanSession = true
        });

        action.Should().Be(PrimaryAction.CheckAndRecover);
    }

    [Fact]
    public void EndSessionHasPriorityOverContinueSetup()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasSetupInterrupted = true,
            IsSessionRunning = true
        });

        action.Should().Be(PrimaryAction.EndSession);
    }

    [Fact]
    public void PreparePcIfWhpxNotReady()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = false,
            IsUsbConnected = true,
            IsDexTorUsb = true
        });

        action.Should().Be(PrimaryAction.PrepareThisPc);
    }

    [Fact]
    public void RestartRequiredIfWhpxNeedsReboot()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = false,
            NeedsReboot = true,
            IsUsbConnected = true,
            IsDexTorUsb = true
        });

        action.Should().Be(PrimaryAction.RestartRequired);
    }

    [Fact]
    public void RestartRequiredHasPriorityOverSessionRunning()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = false,
            NeedsReboot = true,
            IsSessionRunning = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasUncleanSession = false
        });

        action.Should().Be(PrimaryAction.RestartRequired);
    }

    [Fact]
    public void RestartRequiredHasPriorityOverSetupContinuation()
    {
        var action = PrimaryActionResolver.Resolve(new LauncherContext
        {
            IsWhpxEnabled = false,
            NeedsReboot = true,
            IsUsbConnected = true,
            IsDexTorUsb = true,
            HasSetupInterrupted = true
        });

        action.Should().Be(PrimaryAction.RestartRequired);
    }
}
