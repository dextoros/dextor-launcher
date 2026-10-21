using FluentAssertions;
using Xunit;

using DexTor.Creator;

namespace DexTor.Creator.Tests;

public class CreatorTransactionTests
{
    [Fact]
    public void ResumeStartsAfterInterrupt()
    {
        var tx = new CreatorTransaction();
        tx.MarkCompleted(CreatorStep.PrepareRelease);
        tx.MarkCompleted(CreatorStep.ConfirmErase);
        tx.Interrupt();
        tx.Current.Should().Be(CreatorStep.FormatUsb);
        tx.CanResume.Should().BeTrue();
    }

    [Fact]
    public void ResumeFromResetsInterruptAndSetsCurrentAfterStep()
    {
        var tx = new CreatorTransaction();
        tx.MarkCompleted(CreatorStep.PrepareRelease);
        tx.MarkCompleted(CreatorStep.ConfirmErase);
        tx.Interrupt();
        tx.ResumeFrom(CreatorStep.ConfirmErase);

        tx.IsInterrupted.Should().BeFalse();
        tx.Current.Should().Be(CreatorStep.FormatUsb);
        tx.CanResume.Should().BeTrue();
    }

    [Fact]
    public void CannotGoBackInFlow()
    {
        var tx = new CreatorTransaction();
        tx.MarkCompleted(CreatorStep.PrepareRelease);
        tx.MarkCompleted(CreatorStep.ConfirmErase);
        tx.MarkCompleted(CreatorStep.FormatUsb);
        tx.IsStepAllowed(CreatorStep.ConfirmErase).Should().BeFalse();
    }
}
