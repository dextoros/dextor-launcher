using FluentAssertions;
using Xunit;

using DexTor.Qemu;
using DexTor.SessionHost;

namespace DexTor.Launcher.Tests;

public sealed class VmProfileSelectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-profile-" + Guid.NewGuid().ToString("N"));
    private readonly string _qemu;
    private readonly List<string> _probed = [];

    public VmProfileSelectorTests()
    {
        Directory.CreateDirectory(_root);
        _qemu = Path.Combine(_root, "qemu-system-x86_64.exe");
        File.WriteAllText(_qemu, "fake");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private VmProfileSelector Selector(params string[] accepted) => new(
        VmProfileSelector.FastCandidates,
        Path.Combine(_root, "vm-profile.json"),
        (_, profile, _) =>
        {
            _probed.Add(profile.Name);
            return Task.FromResult(accepted.Contains(profile.Name));
        });

    [Fact]
    public async Task TheFastestProfileQemuTakesIsChosenAndTheProbeRunsOnlyOnce()
    {
        (await Selector("host").SelectAsync(_qemu, CancellationToken.None)).Name.Should().Be("host");
        (await Selector("host").SelectAsync(_qemu, CancellationToken.None)).Name.Should().Be("host");

        _probed.Should().Equal("host");
    }

    [Fact]
    public async Task AProfileQemuRefusesFallsBackToTheOneEveryComputerTakes()
    {
        (await Selector().SelectAsync(_qemu, CancellationToken.None)).Should().Be(QemuCompatibilityProfile.Compatible);
        _probed.Should().Equal("host", "max");
    }

    [Fact]
    public async Task ARefusedProfileIsNotOfferedAgainUntilQemuChanges()
    {
        var selector = Selector("host");
        (await selector.SelectAsync(_qemu, CancellationToken.None)).Name.Should().Be("host");

        selector.Refuse(_qemu, new QemuCompatibilityProfile("host", "host"));
        (await selector.SelectAsync(_qemu, CancellationToken.None)).Should().Be(QemuCompatibilityProfile.Compatible);

        // A different QEMU build is asked afresh.
        File.WriteAllText(_qemu, "another build");
        File.SetLastWriteTimeUtc(_qemu, DateTime.UtcNow.AddMinutes(5));
        (await selector.SelectAsync(_qemu, CancellationToken.None)).Name.Should().Be("host");
    }

    [Fact]
    public void TheCompatibleProfileIsNeverRefused()
    {
        var selector = Selector();
        selector.Refuse(_qemu, QemuCompatibilityProfile.Compatible);

        File.Exists(Path.Combine(_root, "vm-profile.json")).Should().BeFalse();
    }

    [Fact]
    public void TheProfilesCpuReachesQemuAndTheCompatibleOneAddsNone()
    {
        var iso = Path.Combine(_root, "DexTor OS.iso");
        var data = Path.Combine(_root, "data.img");
        File.WriteAllText(iso, "x");
        File.WriteAllText(data, "x");
        var request = new DexTor.Contracts.SessionStartRequest(Guid.NewGuid(), iso, iso, data, new string('a', 64), 8192, 8);
        var image = new ResolvedSystemImage(iso, SystemImageLocation.TemporaryCache);

        QemuLaunchPlanBuilder.Build(_qemu, image, request, new QemuCompatibilityProfile("host", "host"))
            .Arguments.Should().ContainInOrder("-cpu", "host");
        QemuLaunchPlanBuilder.Build(_qemu, image, request).Arguments.Should().NotContain("-cpu");
    }

    [Fact]
    public void ANormalStartUsesTheProfileEveryComputerTakesUnlessAFastOneIsAskedFor()
    {
        // -cpu host hung a guest in the firmware on an Alder Lake; the default adds only the
        // x86-64-v2 instructions and AES-NI, and plain qemu64 stays behind it.
        if (Environment.GetEnvironmentVariable("DEXTOR_VM_CPU") is { Length: > 0 }) return;
        VmProfileSelector.DefaultCandidates.Should().Equal(VmProfileSelector.BaselineV2, QemuCompatibilityProfile.Compatible);
        VmProfileSelector.BaselineV2.CpuModel.Should().NotContain("avx").And.NotContain("host").And.Contain("+aes");
    }
}
