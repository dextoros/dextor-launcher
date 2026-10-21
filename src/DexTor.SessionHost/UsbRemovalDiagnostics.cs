using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using DexTor.Contracts;

namespace DexTor.SessionHost;

/// <summary>
/// A read-only explanation of what the production resolver decides about one explicitly
/// chosen storage path, and why.
/// </summary>
/// <remarks>
/// This exists so a removal that is refused on real hardware can be understood without
/// guessing. It reuses <see cref="RemovableTargetResolver"/> — the same algorithm the
/// product runs — rather than describing a second one that could drift from it.
/// <para>
/// It does nothing. There is no flush, no eject, no dismount, no format, no repair, no
/// provisioning and no writable open; the only handles it takes are the read-only queries
/// the resolver already makes. The target is always named by the caller: there is
/// deliberately no "first removable disk" selection, because a diagnostic that picks a
/// device for you is a diagnostic that can point at the wrong one.
/// </para>
/// <para>
/// The report goes back to the caller as text. Writing it anywhere is the caller's
/// decision, and it must be a host path — never the device being examined.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class UsbRemovalDiagnostics
{
    /// <summary>
    /// Describes the chosen path. <paramref name="dataImagePath"/> is required and is
    /// never inferred.
    /// </summary>
    public static string Describe(string dataImagePath, INativeDeviceTopology? topology = null)
    {
        if (string.IsNullOrWhiteSpace(dataImagePath))
        {
            throw new ArgumentException(
                "A diagnostic needs an explicitly chosen storage path; it will not pick a device.",
                nameof(dataImagePath));
        }

        var native = topology ?? new WindowsDeviceTopology();
        var report = new StringBuilder();
        report.AppendLine("DexTor USB removal diagnostics (read-only)");
        report.AppendLine(CultureInfo.InvariantCulture, $"Generated (UTC): {DateTimeOffset.UtcNow:O}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Requested path : {dataImagePath}");
        report.AppendLine();

        AppendContext(report);
        var volume = AppendStorageIdentity(report, native, dataImagePath);
        AppendDeviceMapping(report, native, volume);
        AppendDecision(report, native, dataImagePath);

        report.AppendLine();
        report.AppendLine("No flush, eject, dismount, repair or write was performed.");
        return report.ToString();
    }

    private static void AppendContext(StringBuilder report)
    {
        report.AppendLine("== Session context ==");
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            report.AppendLine(CultureInfo.InvariantCulture, $"User             : {identity.Name}");
            report.AppendLine(CultureInfo.InvariantCulture, $"Elevated         : {principal.IsInRole(WindowsBuiltInRole.Administrator)}");
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"User             : unavailable ({exception.Message})");
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"Interactive      : {Environment.UserInteractive}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Session id       : {Environment.ProcessId}");
        report.AppendLine();
    }

    private static string? AppendStorageIdentity(StringBuilder report, INativeDeviceTopology native, string dataImagePath)
    {
        report.AppendLine("== Storage identity ==");
        var volume = native.VolumeGuidPathFor(dataImagePath);
        if (!volume.Succeeded)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Volume           : unresolved ({volume.Error})");
            report.AppendLine();
            return null;
        }

        report.AppendLine(CultureInfo.InvariantCulture, $"Volume           : {volume.Value}");

        var facts = native.QueryVolume(volume.Value);
        report.AppendLine(CultureInfo.InvariantCulture, $"Disks            : {Describe(facts.DiskNumbers)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Removable media  : {Describe(facts.RemovableMedia)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Bus type         : {Describe(facts.BusType)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"System disk      : {Describe(native.SystemDiskNumber())}");
        report.AppendLine();
        return volume.Value;
    }

    private static void AppendDeviceMapping(StringBuilder report, INativeDeviceTopology native, string? volume)
    {
        report.AppendLine("== Device mapping ==");
        if (volume is null)
        {
            report.AppendLine("Skipped: the volume could not be resolved.");
            report.AppendLine();
            return;
        }

        var disks = native.EnumeratePresentDisks();
        if (!disks.Succeeded || disks.Value is not { } present)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Enumeration      : failed ({disks.Error})");
            report.AppendLine();
            return;
        }

        foreach (var disk in present)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Disk {Describe(disk.DiskNumber),-18} {disk.InstanceId}");
            var parent = native.Parent(disk.DevInst);
            if (parent.Succeeded && parent.HasValue)
            {
                var parentId = native.InstanceId(parent.Value);
                var children = native.Children(parent.Value);
                report.AppendLine(CultureInfo.InvariantCulture, $"  parent         {Describe(parentId)}");
                report.AppendLine(CultureInfo.InvariantCulture, $"  parent holds   {(children.Succeeded && children.Value is { } list ? list.Length.ToString(CultureInfo.InvariantCulture) : "unknown: " + children.Error)}");
            }
            else
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"  parent         {(parent.IsAbsent ? "none" : "unknown: " + parent.Error)}");
            }
        }

        report.AppendLine();
    }

    private static void AppendDecision(StringBuilder report, INativeDeviceTopology native, string dataImagePath)
    {
        report.AppendLine("== Proposed removal target ==");
        var resolution = new RemovableTargetResolver(native).Resolve(dataImagePath);
        if (resolution.Resolved && resolution.Target is { } target)
        {
            report.AppendLine("Decision         : accepted");
            report.AppendLine(CultureInfo.InvariantCulture, $"Device instance  : {target.DeviceInstanceId}");
            report.AppendLine(CultureInfo.InvariantCulture, $"Volume           : {target.VolumeGuidPath}");
            report.AppendLine();
            report.AppendLine("Nothing was asked of Windows; this is what a removal request would name.");
            return;
        }

        report.AppendLine("Decision         : refused");
        report.AppendLine(CultureInfo.InvariantCulture, $"Reason           : {resolution.Reason}");
    }

    private static string Describe<T>(NativeAnswer<T> answer) =>
        !answer.Succeeded ? $"unknown ({answer.Error})"
        : !answer.HasValue ? "none"
        : answer.Value is uint[] numbers ? string.Join(", ", numbers)
        : answer.Value?.ToString() ?? "none";
}
