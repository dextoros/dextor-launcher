using FluentAssertions;
using Xunit;

namespace DexTor.Launcher.Tests;

public sealed class TemporaryCacheCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dextor-janitor-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private string Write(string relative, int bytes = 16, DateTime? writtenUtc = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        if (writtenUtc is { } written) File.SetLastWriteTimeUtc(path, written);
        return path;
    }

    [Fact]
    public void LeftoversAreRemovedButThePartialImageIsKeptForTheDownloadToResume()
    {
        var partial = Write(@"cache\DextorOS_1_1_30.iso.tmp", 4096);
        var partialSource = Write(@"cache\DextorOS_1_1_30.iso.tmp.source");
        var probe = Write(@"cache\.write-test-0123.tmp", 32);
        var releasePartial = Write(@"Release\system.img.partial", 64);
        var image = Write(@"cache\DextorOS_1_1_30.iso", 8192);

        var report = TemporaryCacheCleaner.Clean(_root, Now);

        File.Exists(partial).Should().BeTrue();
        File.Exists(partialSource).Should().BeTrue();
        File.Exists(image).Should().BeTrue();
        File.Exists(probe).Should().BeFalse();
        File.Exists(releasePartial).Should().BeFalse();
        report.FilesRemoved.Should().Be(2);
        report.BytesFreed.Should().Be(32 + 64);
    }

    [Fact]
    public void AbandonedRuntimeUnpacksAndOldVersionsGoTheCurrentRuntimeStays()
    {
        var current = $"qemu-{QemuRuntimePreparationService.Version}";
        var kept = Write($@"Runtime\{current}\qemu-system-x86_64.exe");
        var staging = Write($@"Runtime\{current}.staging-abc\qemu-system-x86_64.exe");
        var old = Write(@"Runtime\qemu-10.0.0\qemu-system-x86_64.exe");
        var oldInstaller = Write(@"Runtime\qemu-w64-10.0.0.exe");
        var partialInstaller = Write($@"Runtime\qemu-w64-{QemuRuntimePreparationService.Version}.exe.tmp");

        TemporaryCacheCleaner.Clean(_root, Now);

        File.Exists(kept).Should().BeTrue();
        Directory.Exists(Path.GetDirectoryName(staging)).Should().BeFalse();
        Directory.Exists(Path.GetDirectoryName(old)).Should().BeFalse();
        File.Exists(oldInstaller).Should().BeFalse();
        File.Exists(partialInstaller).Should().BeFalse();
    }

    [Fact]
    public void OldProvisioningRequestsGoRecentOnesAndTheJournalFolderStay()
    {
        var oldRequest = Path.Combine(_root, "Provisioning", Guid.NewGuid().ToString("N"));
        Write(Path.Combine(oldRequest, "request.json"));
        Directory.SetLastWriteTimeUtc(oldRequest, Now - TimeSpan.FromHours(3));
        var recentRequest = Path.Combine(_root, "Provisioning", Guid.NewGuid().ToString("N"));
        Write(Path.Combine(recentRequest, "request.json"));
        Directory.SetLastWriteTimeUtc(recentRequest, Now - TimeSpan.FromMinutes(5));
        var journal = Write(@"Provisioning\journal\" + Guid.NewGuid().ToString("N") + ".json", writtenUtc: Now - TimeSpan.FromDays(90));

        TemporaryCacheCleaner.Clean(_root, Now);

        Directory.Exists(oldRequest).Should().BeFalse();
        Directory.Exists(recentRequest).Should().BeTrue();
        File.Exists(journal).Should().BeTrue("a lone journal is kept however old: it explains a failure");
    }

    [Fact]
    public void JournalsGoOnlyWhenBothOldAndBeyondTheKeptCount()
    {
        var journals = Enumerable.Range(0, TemporaryCacheCleaner.JournalsKept + 5)
            .Select(index => Write(@"Provisioning\journal\" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".json",
                writtenUtc: Now - TimeSpan.FromDays(40 + index)))
            .ToArray();

        TemporaryCacheCleaner.Clean(_root, Now);

        journals.Take(TemporaryCacheCleaner.JournalsKept).Should().OnlyContain(path => File.Exists(path));
        journals.Skip(TemporaryCacheCleaner.JournalsKept).Should().OnlyContain(path => !File.Exists(path));
    }

    [Fact]
    public void AFileStillOpenIsSkippedNotForced()
    {
        var partial = Write(@"LiveImage\.write-test-busy.tmp");
        using (new FileStream(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var action = () => TemporaryCacheCleaner.Clean(_root, Now);
            action.Should().NotThrow();
        }

        File.Exists(partial).Should().BeTrue();
    }

    [Fact]
    public void OtherImageVersionsAreRemovedOnceTheCurrentOneIsKnown()
    {
        var old = Write(@"LiveImage\DextorOS_1_1_29.iso");
        var oldPartial = Write(@"LiveImage\DextorOS_1_1_29.iso.tmp");
        var oldPartialSource = Write(@"LiveImage\DextorOS_1_1_29.iso.tmp.source");
        var current = Write(@"LiveImage\DextorOS_1_1_30.iso");
        var currentPartial = Write(@"LiveImage\DextorOS_1_1_30.iso.tmp");

        TemporaryCacheCleaner.RemoveOtherImages(Path.Combine(_root, "LiveImage"), "DextorOS_1_1_30.iso");

        File.Exists(old).Should().BeFalse();
        File.Exists(oldPartial).Should().BeFalse();
        File.Exists(oldPartialSource).Should().BeFalse();
        File.Exists(current).Should().BeTrue();
        File.Exists(currentPartial).Should().BeTrue();
    }

    [Fact]
    public void TheImageMovesFromTheOldLiveImageFolderToCacheWithoutADownload()
    {
        var root = Path.Combine(_root, "LocalAppData", "DexTor");
        Write(@"LocalAppData\DexTor\LiveImage\DextorOS_1_1_48.iso", 4096);
        Write(@"LocalAppData\DexTor\LiveImage\DextorOS_1_1_49.iso.tmp", 512);

        TemporaryCacheCleaner.MigrateImageFolder(root);

        File.ReadAllBytes(Path.Combine(root, "cache", "DextorOS_1_1_48.iso")).Should().HaveCount(4096);
        File.Exists(Path.Combine(root, "cache", "DextorOS_1_1_49.iso.tmp")).Should().BeTrue();
        Directory.Exists(Path.Combine(root, "LiveImage")).Should().BeFalse();
    }

    [Fact]
    public void CachesFromTempMoveToTheNewRootAndTheOldFolderIsCleared()
    {
        var legacy = Path.Combine(_root, "Temp", "DexTor");
        var target = Path.Combine(_root, "LocalAppData", "DexTor");
        var image = Write(@"Temp\DexTor\LiveImage\DextorOS_1_1_30.iso", 4096);
        var partial = Write(@"Temp\DexTor\LiveImage\DextorOS_1_1_31.iso.tmp", 512);
        var journal = Write(@"Temp\DexTor\Provisioning\journal\a.log");
        Write(@"Temp\DexTor\Runtime\qemu-11.1.0\qemu-system-x86_64.exe");
        var kept = Write(@"LocalAppData\DexTor\LiveImage\DextorOS_1_1_29.iso", 8);

        TemporaryCacheCleaner.MigrateFromTemp(legacy, target);

        File.ReadAllBytes(Path.Combine(target, "cache", "DextorOS_1_1_30.iso")).Should().HaveCount(4096, "the verified image is not downloaded again");
        File.Exists(Path.Combine(target, "cache", "DextorOS_1_1_31.iso.tmp")).Should().BeTrue("a partial download still resumes");
        File.Exists(Path.Combine(target, "Provisioning", "journal", "a.log")).Should().BeTrue();
        Directory.Exists(Path.Combine(target, "Runtime")).Should().BeFalse("an old runtime has no file list and is downloaded again anyway");
        File.Exists(kept).Should().BeTrue();
        Directory.Exists(legacy).Should().BeFalse("nothing of DexTor is left in %TEMP%");
        File.Exists(image).Should().BeFalse();
        File.Exists(partial).Should().BeFalse();
        File.Exists(journal).Should().BeFalse();
    }

    [Fact]
    public void CachesLiveInTheUsersOwnLocalFolderNotInTemp()
    {
        DexTor.Contracts.DexTorStorage.DefaultRoot.Should().Be(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DexTor"));
        DexTor.Contracts.DexTorStorage.DefaultRoot.Should().NotStartWith(Path.GetTempPath());
    }
    [Fact]
    public void AMissingCacheIsNotAnError()
    {
        TemporaryCacheCleaner.Clean(Path.Combine(_root, "absent"), Now).FilesRemoved.Should().Be(0);
    }
}
