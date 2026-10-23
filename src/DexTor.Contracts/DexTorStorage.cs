namespace DexTor.Contracts;

/// <summary>Where DexTor keeps its caches on the host.</summary>
/// <remarks>
/// Everything lives under <c>%LOCALAPPDATA%\DexTor</c>: the person's own folder, writable
/// without administrator rights, and - unlike <c>%TEMP%</c> - not emptied by Windows on its
/// own. In %TEMP%, Storage Sense and Disk Cleanup had taken a 1.2 GB QEMU runtime apart,
/// and a 2 GB image and the runtime would have been downloaded again every few days.
/// Nothing here is installed: the launcher's own cleanup at start keeps the folder tidy,
/// and deleting it removes everything.
/// </remarks>
public static class DexTorStorage
{
    /// <summary>Overrides the root, for tests only.</summary>
    public static string? RootOverride { get; set; }

    /// <summary>
    /// Where the caches live outside tests: %LOCALAPPDATA%\DexTor, or the package's own
    /// copy of it when the launcher runs from its Store package.
    /// </summary>
    public static string DefaultRoot => ResolveRoot(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        PackagedApp.FamilyName);

    /// <summary>The root for a given %LOCALAPPDATA% and package family (null: not packaged).</summary>
    /// <remarks>
    /// A packaged app that creates %LOCALAPPDATA%\DexTor gets it redirected by Windows to
    /// %LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local\DexTor, and only processes of
    /// that package see the redirection. The elevated USB provisioner may run without the
    /// package identity, and would look for request, image and journal in a real
    /// %LOCALAPPDATA%\DexTor that does not exist. So a packaged launcher names the
    /// redirected folder itself: the same files for every process, and Windows still
    /// removes them with the app.
    /// </remarks>
    public static string ResolveRoot(string localApplicationData, string? packageFamilyName) =>
        string.IsNullOrEmpty(packageFamilyName)
            ? Path.Combine(localApplicationData, "DexTor")
            : Path.Combine(localApplicationData, "Packages", packageFamilyName, "LocalCache", "Local", "DexTor");

    public static string Root => RootOverride ?? DefaultRoot;

    /// <summary>The downloaded DexTor OS image: <c>%LOCALAPPDATA%DexTorche</c>.</summary>
    public static string LiveImage => Path.Combine(Root, LiveImageFolder);

    /// <summary>The image folder's name; it was "LiveImage" before 0.1.39.</summary>
    public const string LiveImageFolder = "cache";

    /// <summary>Where launchers before 0.1.39 kept the image, inside <see cref="Root"/>.</summary>
    public const string LegacyLiveImageFolder = "LiveImage";

    public static string Runtime => Path.Combine(Root, "Runtime");

    public static string Release => Path.Combine(Root, "Release");

    public static string Provisioning => Path.Combine(Root, "Provisioning");

    public static string Journal => Path.Combine(Provisioning, "journal");

    /// <summary>Where earlier launchers kept the same caches.</summary>
    public static string LegacyTempRoot => Path.Combine(Path.GetTempPath(), "DexTor");
}
