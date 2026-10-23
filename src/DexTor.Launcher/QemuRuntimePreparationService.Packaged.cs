using DexTor.Contracts;

namespace DexTor.Launcher;

/// <summary>
/// The Store package's rule for the QEMU runtime: it comes with the package, or not at all.
/// </summary>
/// <remarks>
/// Microsoft Store does not accept an app that downloads and runs executable code it did
/// not ship. The package carries QEMU in runtime\qemu beside the launcher, where
/// <see cref="Inspect"/> finds it first; should it be missing or damaged, a packaged
/// launcher reports that and never falls back to fetching the installer from the web.
/// The portable and MSI launchers keep the download.
/// </remarks>
internal sealed partial class QemuRuntimePreparationService
{
    internal const string PackagedRuntimeMissingMessage =
        "The virtualization runtime that comes with DexTor OS is missing or damaged. Repair or reinstall the app from Microsoft Store (Settings > Apps > Installed apps > DexTor OS > Advanced options > Repair).";

    /// <summary>Whether this launcher runs from its Store package; replaceable in tests.</summary>
    internal Func<bool> IsPackaged { get; init; } = static () => PackagedApp.IsPackaged;

    private bool MustNotDownload(QemuRuntimeInspectionResult inspection, out QemuRuntimeInspectionResult failure)
    {
        if (!IsPackaged())
        {
            failure = inspection;
            return false;
        }

        failure = inspection with { RuntimePath = _bundledRuntimePath, ErrorMessage = PackagedRuntimeMissingMessage };
        return true;
    }
}
