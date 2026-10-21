using System.Diagnostics;
using System.Text.Json;
using DexTor.Contracts;
using DexTor.Qemu;

namespace DexTor.SessionHost;

/// <summary>
/// Picks the fastest way of starting the VM this computer accepts, and remembers it.
/// </summary>
/// <remarks>
/// QEMU's default CPU runs everywhere but hides AES-NI and AVX from the guest, which made
/// DexTor OS sluggish. A faster CPU model is tried first with a short probe - QEMU on its
/// own, with no disk, no network and no window, so nothing on the stick is touched - and
/// one that QEMU refuses is skipped. The answer is kept per QEMU build in
/// <c>%LOCALAPPDATA%\DexTor\vm-profile.json</c>, so the probe runs once, not every start.
/// A profile that later fails a real start, or whose guest dies right after starting, is
/// recorded as refused and the next start falls back to the next one.
/// </remarks>
public sealed class VmProfileSelector(
    IReadOnlyList<QemuCompatibilityProfile> candidates,
    string storePath,
    Func<string, QemuCompatibilityProfile, CancellationToken, Task<bool>> probe)
{
    /// <summary>
    /// The profiles a normal start tries: qemu64 with the x86-64-v2 instructions and AES-NI,
    /// then plain qemu64.
    /// </summary>
    /// <remarks>
    /// "host" and "max" were tried first in 0.1.22-0.1.23: faster on the Ryzen they were
    /// measured on, but on an Intel Alder Lake the guest hung in the firmware - "Display
    /// output is not active" for good - with QEMU still running. The first profile now only
    /// adds what every x86-64 processor of the last fifteen years has and WHPX passes through
    /// (SSSE3, SSE4.1/4.2, POPCNT, AES-NI, PCLMULQDQ), nothing of AVX or later; AES-NI is
    /// what Tor and the encrypted storage gain from. And a firmware hang is now seen: if the
    /// guest has drawn nothing 20 s after start, the start is redone with plain qemu64 and
    /// the profile is not offered again. <c>DEXTOR_VM_CPU</c> still names one to try first.
    /// </remarks>
    public static IReadOnlyList<QemuCompatibilityProfile> DefaultCandidates =>
        Environment.GetEnvironmentVariable("DEXTOR_VM_CPU") is { Length: > 0 } cpu
            && cpu.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ',' or '+')
            ? [new QemuCompatibilityProfile(cpu, cpu), BaselineV2, QemuCompatibilityProfile.Compatible]
            : [BaselineV2, QemuCompatibilityProfile.Compatible];

    public static readonly QemuCompatibilityProfile BaselineV2 =
        new("x86-64-v2-aes", "qemu64,+ssse3,+sse4.1,+sse4.2,+popcnt,+aes,+pclmulqdq");

    /// <summary>The fast profiles, kept for when they are proven: fastest first.</summary>
    public static readonly IReadOnlyList<QemuCompatibilityProfile> FastCandidates =
    [
        new("host", "host"),
        new("max", "max"),
        QemuCompatibilityProfile.Compatible
    ];

    public static VmProfileSelector CreateDefault() => new(
        DefaultCandidates,
        Path.Combine(DexTorStorage.Root, "vm-profile.json"),
        ProbeAsync);

    public async Task<QemuCompatibilityProfile> SelectAsync(string qemuExecutable, CancellationToken cancellationToken)
    {
        var build = BuildIdentity(qemuExecutable);
        var store = Read();
        if (store.Build != build)
        {
            store = new Store(build, null, []);
        }

        if (store.Chosen is { } chosen
            && candidates.FirstOrDefault(candidate => candidate.Name == chosen) is { } remembered
            && !store.Refused.Contains(chosen))
        {
            return remembered;
        }

        foreach (var candidate in candidates)
        {
            if (store.Refused.Contains(candidate.Name)) continue;
            if (ReferenceEquals(candidate, candidates[^1])
                || await probe(qemuExecutable, candidate, cancellationToken).ConfigureAwait(false))
            {
                Write(store with { Chosen = candidate.Name });
                return candidate;
            }

            store = store with { Refused = [.. store.Refused, candidate.Name] };
        }

        return candidates[^1];
    }

    /// <summary>Never offers <paramref name="profile"/> again on this QEMU build.</summary>
    public void Refuse(string qemuExecutable, QemuCompatibilityProfile profile)
    {
        if (ReferenceEquals(profile, candidates[^1]) || profile.Name == candidates[^1].Name) return;
        var build = BuildIdentity(qemuExecutable);
        var store = Read();
        if (store.Build != build) store = new Store(build, null, []);
        Write(store with
        {
            Chosen = store.Chosen == profile.Name ? null : store.Chosen,
            Refused = [.. store.Refused.Append(profile.Name).Distinct()]
        });
    }

    /// <summary>Whether QEMU starts with the profile's CPU under WHPX at all.</summary>
    internal static async Task<bool> ProbeAsync(string qemuExecutable, QemuCompatibilityProfile profile, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(qemuExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true
        };
        foreach (var argument in new[] { "-accel", "whpx", "-machine", "pc", "-m", "256", "-nodefaults", "-display", "none", "-S" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (profile.CpuModel is { } cpu)
        {
            startInfo.ArgumentList.Add("-cpu");
            startInfo.ArgumentList.Add(cpu);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            // A CPU model QEMU or WHPX refuses ends the process within a moment; one it
            // takes leaves QEMU waiting, paused, for a guest that is never started.
            var exited = process.WaitForExitAsync(cancellationToken);
            var settled = await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false);
            if (settled == exited) return false;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string BuildIdentity(string qemuExecutable)
    {
        try
        {
            var file = new FileInfo(qemuExecutable);
            return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private sealed record Store(string Build, string? Chosen, string[] Refused);

    private Store Read()
    {
        try
        {
            return File.Exists(storePath)
                ? JsonSerializer.Deserialize<Store>(File.ReadAllText(storePath)) ?? new Store(string.Empty, null, [])
                : new Store(string.Empty, null, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Store(string.Empty, null, []);
        }
    }

    private void Write(Store store)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
            File.WriteAllText(storePath, JsonSerializer.Serialize(store));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not remembered: the probe simply runs again next time.
        }
    }
}
