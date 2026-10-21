using System;
using System.Collections.Generic;

namespace DexTor.Creator;

public sealed class CreatorTransaction
{
    private static readonly CreatorStep[] Flow = new[]
    {
        CreatorStep.PrepareRelease,
        CreatorStep.ConfirmErase,
        CreatorStep.FormatUsb,
        CreatorStep.WriteSystemImage,
        CreatorStep.CreateDataImage,
        CreatorStep.VerifyUsb,
        CreatorStep.ProvisionGuest,
        CreatorStep.Complete
    };

    public Guid Id { get; } = Guid.NewGuid();
    public CreatorStep LastCompleted { get; private set; } = CreatorStep.None;
    public bool IsInterrupted { get; private set; }

    public CreatorTransaction()
    {
    }

    public CreatorTransaction(CreatorStep lastCompleted, bool isInterrupted = false)
    {
        if (FindIndex(lastCompleted) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastCompleted), lastCompleted, "Unknown creator step");
        }

        LastCompleted = lastCompleted;
        IsInterrupted = isInterrupted;
    }

    public CreatorStep Current
    {
        get
        {
            if (LastCompleted == CreatorStep.Complete)
            {
                return CreatorStep.Complete;
            }

            var index = FindIndex(LastCompleted);
            return Flow[index + 1];
        }
    }

    public bool CanResume => IsInterrupted || LastCompleted != CreatorStep.Complete;

    public void MarkCompleted(CreatorStep step)
    {
        var index = FindIndex(step);
        if (index < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown creator step");
        }

        if (index < FindIndex(LastCompleted))
        {
            return;
        }

        LastCompleted = step;
    }

    public void Interrupt() => IsInterrupted = true;

    public void ResumeFrom(CreatorStep step)
    {
        if (FindIndex(step) < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(step), step, "Unknown creator step");
        }

        LastCompleted = step;
        IsInterrupted = false;
    }

    public bool IsStepAllowed(CreatorStep step)
    {
        var currentIndex = FindIndex(LastCompleted) + 1;
        var targetIndex = FindIndex(step);
        if (currentIndex < 0 || targetIndex < 0)
        {
            return false;
        }

        return targetIndex >= currentIndex;
    }

    private static int FindIndex(CreatorStep step)
    {
        for (var i = 0; i < Flow.Length; i++)
        {
            if (Flow[i] == step)
            {
                return i;
            }
        }

        return -1;
    }
}
