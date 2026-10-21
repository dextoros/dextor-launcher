namespace DexTor.SessionHost;

/// <summary>How much of this computer the virtual machine is given.</summary>
/// <remarks>
/// A fixed 4 GB was too little for GNOME, Tor and a browser on a computer with plenty to
/// spare, and too much on one with 8 GB. Three eighths of the memory are taken, within 4 to
/// 8 GB - 6 GB of 16, where a quarter (4 GB) left the live system short - so Windows keeps
/// at least five eighths; below 8 GB (the host check
/// already warns there) the VM makes do with 3 GB. Half the logical processors, 2 to 8.
/// </remarks>
public static class VmResources
{
    public const int MinimumMemoryMiB = 3072;
    public const int SmallestNormalMemoryMiB = 4096;
    public const int LargestMemoryMiB = 8192;

    public static (int MemoryMiB, int Processors) ForHost(long totalMemoryBytes, int logicalProcessors)
    {
        var totalMiB = totalMemoryBytes / (1024 * 1024);
        int memory;
        if (totalMiB <= 0)
        {
            memory = SmallestNormalMemoryMiB;
        }
        else if (totalMiB < 8L * 1024)
        {
            memory = MinimumMemoryMiB;
        }
        else
        {
            // Whole 512 MiB steps, so the guest sees a round figure.
            memory = (int)Math.Clamp(totalMiB * 3 / 8 / 512 * 512, SmallestNormalMemoryMiB, LargestMemoryMiB);
        }

        return (memory, Math.Clamp(logicalProcessors / 2, 2, 8));
    }

    public static (int MemoryMiB, int Processors) ForThisComputer() =>
        ForHost(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, Environment.ProcessorCount);
}
