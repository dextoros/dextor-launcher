using FluentAssertions;
using Xunit;

using DexTor.Domain;

namespace DexTor.Domain.Tests;

public class StateMachineTests
{
    [Fact]
    public void StartingToHostCheckIsDeterministic()
    {
        var fsm = new DexTorStateMachine();
        fsm.Apply(BetaEvent.Start);
        fsm.State.Should().Be(BetaState.SelfCheck);
        fsm.Apply(BetaEvent.HostCheckPassed);
        fsm.State.Should().Be(BetaState.HostCheck);
        fsm.CanApply(BetaEvent.HostCheckPassed).Should().BeFalse();
    }

    [Fact]
    public void InvalidTransitionThrows()
    {
        var fsm = new DexTorStateMachine(BetaState.SafeToRemove);
        Action act = () => fsm.Apply(BetaEvent.LaunchStarted);
        act.Should().Throw<BetaStateTransitionException>();
    }

    [Fact]
    public void HostCheckPreparesImageBeforeWaitingForUsb()
    {
        var fsm = new DexTorStateMachine(BetaState.HostCheck);

        fsm.Apply(BetaEvent.Continue).Should().Be(BetaState.ImagePreparation);
        fsm.Apply(BetaEvent.DownloadPrepared).Should().Be(BetaState.WaitingForUsb);
        fsm.CanApply(BetaEvent.DownloadPrepared).Should().BeFalse();
    }
}
