using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DexTor.Platform.Windows;

public sealed record UsbDevice(string DeviceId, string InstanceId, ulong CapacityBytes, string Description);

public interface IWindowsHostProbe
{
    Task<IReadOnlyList<UsbDevice>> EnumerateUsbAsync(CancellationToken cancellationToken = default);
    Task<bool> IsWhpxEnabledAsync(CancellationToken cancellationToken = default);
    Task<bool> EnableWhpxAsync(CancellationToken cancellationToken = default);
}

public interface IUsbSafety
{
    Task<bool> IsUsbSafeForProvisioningAsync(string instanceId, CancellationToken cancellationToken = default);
}

public sealed partial class WindowsHostProbe : IWindowsHostProbe, IUsbSafety
{
    internal const string WhpxFeatureName = "HypervisorPlatform";

    public Task<IReadOnlyList<UsbDevice>> EnumerateUsbAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = DriveInfo.GetDrives()
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Removable)
            .Select(drive => TryCreateUsbDevice(drive))
            .Where(d => d is not null)
            .OfType<UsbDevice>()
            .ToArray();
        return Task.FromResult((IReadOnlyList<UsbDevice>)candidates);
    }

    public Task<bool> IsWhpxEnabledAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsWhpxEnabled());
    }

    public Task<bool> EnableWhpxAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(EnableWhpx());
    }

    public Task<bool> IsUsbSafeForProvisioningAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(IsUsbSafeForProvisioning(instanceId));
    }

    private static UsbDevice? TryCreateUsbDevice(DriveInfo drive)
    {
        try
        {
            var root = drive.RootDirectory.FullName;
            var fullPath = Path.GetFullPath(root);
            var capacity = Math.Max(0UL, (ulong)drive.TotalSize);
            var description = BuildDescription(drive);
            return new UsbDevice(fullPath, fullPath, capacity, description);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildDescription(DriveInfo drive)
    {
        try
        {
            var volume = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? string.Empty : drive.VolumeLabel.Trim();
            return string.IsNullOrWhiteSpace(volume)
                ? $"{drive.DriveFormat} {drive.RootDirectory.FullName}"
                : $"{drive.DriveFormat} {volume}";
        }
        catch
        {
            return drive.DriveType.ToString();
        }
    }

    private static bool IsUsbSafeForProvisioning(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
        {
            return false;
        }

        var normalized = NormalizeRoot(instanceId);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        try
        {
            var candidates = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Removable)
                .Where(d => string.Equals(
                    NormalizeRoot(d.RootDirectory.FullName),
                    normalized,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (candidates.Length != 1)
            {
                return false;
            }

            var drive = candidates[0];
            var probeFile = Path.Combine(drive.RootDirectory.FullName, $".dextor-probe-{Guid.NewGuid():N}");
            try
            {
                using var stream = new FileStream(probeFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.WriteByte(0);
            }
            finally
            {
                if (File.Exists(probeFile))
                {
                    File.Delete(probeFile);
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? NormalizeRoot(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            return Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWhpxEnabled()
    {
        try
        {
            var result = WHvGetCapability(
                0,
                out var hypervisorPresent,
                sizeof(ulong),
                out var writtenSize);
            return IsHypervisorCapabilityPresent(result, hypervisorPresent, writtenSize);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or
            EntryPointNotFoundException or
            BadImageFormatException)
        {
            return false;
        }
    }

    private static bool EnableWhpx()
    {
        return EnableWhpx(RunProcess);
    }

    internal static bool EnableWhpx(Func<ProcessStartInfo, int?> runProcess)
    {
        try
        {
            var enableFeature = new ProcessStartInfo("dism.exe")
            {
                Arguments = $"/Online /Enable-Feature /All /FeatureName:{WhpxFeatureName} /NoRestart /Quiet",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var configureBoot = new ProcessStartInfo("bcdedit.exe")
            {
                Arguments = "/set hypervisorlaunchtype auto",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            return IsSuccessfulExitCode(runProcess(enableFeature)) &&
                IsSuccessfulExitCode(runProcess(configureBoot));
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsHypervisorCapabilityPresent(int hresult, ulong value, uint writtenSize)
    {
        return hresult >= 0 && writtenSize >= sizeof(int) && value != 0;
    }

    private static bool IsSuccessfulExitCode(int? exitCode) => exitCode is 0 or 3010;

    private static int? RunProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        process.WaitForExit();
        return process.ExitCode;
    }

    [LibraryImport("WinHvPlatform.dll", EntryPoint = "WHvGetCapability")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WHvGetCapability(
        uint capabilityCode,
        out ulong capabilityBuffer,
        uint capabilityBufferSizeInBytes,
        out uint writtenSizeInBytes);
}
