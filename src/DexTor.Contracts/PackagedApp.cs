using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace DexTor.Contracts;

/// <summary>
/// Whether this launcher runs from its Microsoft Store (MSIX) package, and which one.
/// </summary>
/// <remarks>
/// <para>
/// The same executable is shipped portable, in the MSI and in the Store package; what
/// differs is decided here at run time, not by a build flag. A packaged launcher must not
/// fetch or run executable code the package did not bring (QEMU ships inside it), leaves
/// its own updates to the Store, and keeps its data in the package's private folder.
/// </para>
/// <para>
/// A process started from the package normally carries the package identity, and
/// GetCurrentPackageFamilyName names it. The elevated USB provisioner is the same
/// executable restarted through the UAC prompt, and an elevated restart is not an
/// activation of the package: it may run without the identity. It still runs from the
/// package's folder, beside the package's AppxManifest.xml, so the family name is then
/// worked out from that manifest - the two processes agree either way.
/// </para>
/// </remarks>
public static partial class PackagedApp
{
    private static readonly Lazy<string?> DetectedFamilyName = new(Detect);

    /// <summary>Replaces detection, for tests only; an empty string means "not packaged".</summary>
    public static string? FamilyNameOverride { get; set; }

    /// <summary>The package family name (Name_PublisherId), or null outside a package.</summary>
    public static string? FamilyName => FamilyNameOverride is { } forced
        ? (forced.Length == 0 ? null : forced)
        : DetectedFamilyName.Value;

    /// <summary>True when this executable belongs to an installed MSIX package.</summary>
    public static bool IsPackaged => FamilyName is not null;

    private static string? Detect()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return CurrentPackageFamilyName()
                ?? FamilyNameFromManifest(Path.Combine(AppContext.BaseDirectory, "AppxManifest.xml"));
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    private static unsafe string? CurrentPackageFamilyName()
    {
        uint length = 0;
        var result = GetCurrentPackageFamilyName(ref length, null);
        // APPMODEL_ERROR_NO_PACKAGE (15700) when the process has no identity.
        if (result != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* name = buffer)
        {
            return GetCurrentPackageFamilyName(ref length, name) == ErrorSuccess
                ? new string(name, 0, (int)length - 1)
                : null;
        }
    }

    /// <summary>
    /// The family name of the package whose manifest is at <paramref name="manifestPath"/>,
    /// or null when there is no such manifest.
    /// </summary>
    internal static string? FamilyNameFromManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var identity = XDocument.Load(manifestPath).Root?
                .Elements().FirstOrDefault(element => element.Name.LocalName == "Identity");
            var name = identity?.Attribute("Name")?.Value;
            var publisher = identity?.Attribute("Publisher")?.Value;
            return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher)
                ? null
                : FamilyNameFromId(name, publisher);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Windows' own Name + Publisher to family name mapping (PackageFamilyNameFromId).</summary>
    internal static unsafe string? FamilyNameFromId(string name, string publisher)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        fixed (char* namePointer = name)
        fixed (char* publisherPointer = publisher)
        {
            var id = new PackageId { Name = namePointer, Publisher = publisherPointer };
            uint length = 0;
            if (PackageFamilyNameFromId(ref id, ref length, null) != ErrorInsufficientBuffer || length == 0)
            {
                return null;
            }

            var buffer = new char[length];
            fixed (char* familyName = buffer)
            {
                return PackageFamilyNameFromId(ref id, ref length, familyName) == ErrorSuccess
                    ? new string(familyName, 0, (int)length - 1)
                    : null;
            }
        }
    }

    /// <summary>PACKAGE_ID from appmodel.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct PackageId
    {
        public uint Reserved;
        public uint ProcessorArchitecture;
        public ulong Version;
        public char* Name;
        public char* Publisher;
        public char* ResourceId;
        public char* PublisherId;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, char* packageFamilyName);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int PackageFamilyNameFromId(ref PackageId packageId, ref uint packageFamilyNameLength, char* packageFamilyName);
}
