using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DexTor.Contracts;
using Microsoft.Win32.SafeHandles;
using static DexTor.SessionHost.NativeBuffer;

namespace DexTor.SessionHost;

/// <summary>
/// Shared helper for the <c>WCHAR*</c> out-buffers these APIs fill. A managed
/// <c>char</c> cannot cross the boundary without disabling runtime marshalling for the
/// whole assembly, and the two have identical layout.
/// </summary>
internal static class NativeBuffer
{
    public static ref ushort AsUtf16(char[] buffer) => ref Unsafe.As<char, ushort>(ref buffer[0]);
}

/// <summary>The verified removable target a removal request may name.</summary>
/// <param name="VolumeGuidPath">
/// <c>\\?\Volume{GUID}\</c> for the volume holding <c>data.img</c>. Stable across
/// drive-letter changes, which a stale letter is not.
/// </param>
/// <param name="DeviceInstanceId">The device instance the removal request targets.</param>
public sealed record RemovableTarget(string VolumeGuidPath, string DeviceInstanceId);

/// <summary>Outcome of resolving a data image to one verified removable device.</summary>
public sealed record TargetResolution(RemovableTarget? Target, string? Reason)
{
    public bool Resolved => Target is not null;
}

/// <summary>Flushes the host volume that holds the persistent image.</summary>
public interface IHostVolumeFlusher
{
    /// <summary>
    /// Flushes the whole volume containing <paramref name="dataImagePath"/>.
    /// </summary>
    /// <remarks>
    /// Whole volume, not the file. QEMU's handle is already closed by the time this runs,
    /// so the dirty pages belong to the volume's cache and a file-level flush would move
    /// nothing while looking like it had. A file-only flush must never be reported here.
    /// </remarks>
    Task<(HostFlushResult Result, string? Detail)> FlushVolumeAsync(
        string dataImagePath,
        CancellationToken cancellationToken);
}

/// <summary>Resolves a data image to the removable device that may be ejected.</summary>
public interface IRemovableDeviceResolver
{
    /// <param name="expectedVolumeGuidPath">
    /// The volume identity captured when the session started, when there is one. A path
    /// that now resolves to a different volume is refused rather than followed: between
    /// starting and finalizing a drive letter can be reassigned, and the device answering
    /// to that path may not be the one the session was holding.
    /// </param>
    TargetResolution Resolve(string dataImagePath, string? expectedVolumeGuidPath = null);
}

/// <summary>Asks Windows to remove a verified device.</summary>
public interface IDeviceRemover
{
    Task<(DeviceRemovalResult Result, string? Detail)> RequestRemovalAsync(
        RemovableTarget target,
        CancellationToken cancellationToken);
}

/// <summary>
/// Flushes a volume through <c>FlushFileBuffers</c> on a volume handle.
/// </summary>
/// <remarks>
/// Opening a volume with write access is an administrative operation, so on a normal
/// interactive account this reports <see cref="HostFlushResult.Denied"/>. That is the
/// honest answer: DexTor does not elevate itself, does not install a privileged service,
/// and does not quietly downgrade to a file flush to produce a green result.
/// <para>
/// The native call runs on a thread pool thread. It is never abandoned on a managed
/// timeout — a blocked flush is still running inside the kernel, and pretending otherwise
/// would let ownership be released while the device was still being written.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsVolumeFlusher : IHostVolumeFlusher
{
    private const int ErrorAccessDenied = 5;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    public async Task<(HostFlushResult Result, string? Detail)> FlushVolumeAsync(
        string dataImagePath,
        CancellationToken cancellationToken)
    {
        var volume = TryResolveVolumeRoot(dataImagePath);
        if (volume is null)
        {
            return (HostFlushResult.Unavailable, "The volume holding the persistent image could not be resolved.");
        }

        // A volume handle is opened without the trailing separator.
        var devicePath = volume.TrimEnd('\\');
        return await Task.Run(() => Flush(devicePath), cancellationToken).ConfigureAwait(false);
    }

    private static (HostFlushResult Result, string? Detail) Flush(string devicePath)
    {
        using var handle = CreateFileW(
            devicePath,
            GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorAccessDenied
                ? (HostFlushResult.Denied,
                    "Flushing the USB volume needs Administrator rights, which DexTor does not request.")
                : (HostFlushResult.Failed, Describe("The USB volume could not be opened", error));
        }

        if (!FlushFileBuffers(handle))
        {
            var error = Marshal.GetLastPInvokeError();
            return error == ErrorAccessDenied
                ? (HostFlushResult.Denied, "The USB volume flush was denied.")
                : (HostFlushResult.Failed, Describe("The USB volume flush failed", error));
        }

        return (HostFlushResult.Succeeded, null);
    }

    private static string Describe(string what, int error) =>
        $"{what} (Windows error {error.ToString(CultureInfo.InvariantCulture)}): {new Win32Exception(error).Message}";

    /// <summary>
    /// The <c>\\?\Volume{GUID}\</c> root for the path, or null when there is not exactly
    /// one resolvable local volume.
    /// </summary>
    internal static string? TryResolveVolumeRoot(string dataImagePath)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(dataImagePath);
            var mountPoint = new char[512];
            if (!GetVolumePathNameW(full, ref AsUtf16(mountPoint), (uint)mountPoint.Length))
            {
                return null;
            }

            var root = new string(mountPoint, 0, Array.IndexOf(mountPoint, '\0') is var end and >= 0 ? end : mountPoint.Length);
            var volumeName = new char[128];
            if (!GetVolumeNameForVolumeMountPointW(root, ref AsUtf16(volumeName), (uint)volumeName.Length))
            {
                return null;
            }

            var resolved = new string(volumeName, 0, Array.IndexOf(volumeName, '\0') is var stop and >= 0 ? stop : volumeName.Length);
            return resolved.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ? resolved : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumePathNameW(string fileName, ref ushort buffer, uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetVolumeNameForVolumeMountPointW(string mountPoint, ref ushort buffer, uint bufferLength);
}

/// <summary>
/// Resolves the session's storage to exactly one removable device instance on Windows.
/// </summary>
/// <remarks>
/// The decision logic lives in <see cref="RemovableTargetResolver"/>; this type only
/// supplies the native answers, through <see cref="WindowsDeviceTopology"/>. Splitting
/// them is what lets the actual production algorithm be exercised against recorded
/// topologies — an ordinary stick, one behind a hub, UASP, a composite device, a
/// multi-LUN enclosure — instead of only against whatever is plugged into the machine
/// running the tests.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsRemovableDeviceResolver : IRemovableDeviceResolver
{
    private readonly RemovableTargetResolver _resolver;

    public WindowsRemovableDeviceResolver() : this(new WindowsDeviceTopology())
    {
    }

    internal WindowsRemovableDeviceResolver(INativeDeviceTopology topology) =>
        _resolver = new RemovableTargetResolver(topology);

    public TargetResolution Resolve(string dataImagePath, string? expectedVolumeGuidPath = null) =>
        _resolver.Resolve(dataImagePath, expectedVolumeGuidPath);
}


/// <summary>
/// Asks Windows to remove a device through <c>CM_Request_Device_Eject</c>.
/// </summary>
/// <remarks>
/// This is the supported request, and its answer is preserved as given: a veto keeps its
/// type and, where Windows supplies one, the name of whatever vetoed. A rejected request
/// stays rejected. There is no forced dismount, no killing processes outside DexTor's own
/// job, and no destructive fallback — those would defeat the point of asking.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsDeviceRemover : IDeviceRemover
{
    private const int CrSuccess = 0;
    private const int CrRemoveVetoed = 0x00000017;
    private const int CrAccessDenied = 0x00000033;

    public async Task<(DeviceRemovalResult Result, string? Detail)> RequestRemovalAsync(
        RemovableTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        return await Task.Run(() => Request(target.DeviceInstanceId), cancellationToken).ConfigureAwait(false);
    }

    private static (DeviceRemovalResult Result, string? Detail) Request(string deviceInstanceId)
    {
        var status = CM_Locate_DevNodeW(out var devInst, deviceInstanceId, 0);
        if (status != CrSuccess)
        {
            return (DeviceRemovalResult.TargetUnresolved,
                $"The device could not be located (CM error {status.ToString(CultureInfo.InvariantCulture)}).");
        }

        var vetoName = new char[260];
        var vetoType = 0;
        status = CM_Request_Device_EjectW(devInst, ref vetoType, ref AsUtf16(vetoName), (uint)vetoName.Length, 0);
        if (status == CrSuccess && vetoType == 0)
        {
            return (DeviceRemovalResult.Removed, null);
        }

        var terminator = Array.IndexOf(vetoName, '\0');
        var name = new string(vetoName, 0, terminator >= 0 ? terminator : vetoName.Length);
        return status switch
        {
            CrRemoveVetoed or CrSuccess => (
                DeviceRemovalResult.Vetoed,
                $"Windows refused to remove the device (veto type {vetoType.ToString(CultureInfo.InvariantCulture)}"
                + (string.IsNullOrWhiteSpace(name) ? ")." : $", {name}).")),
            CrAccessDenied => (DeviceRemovalResult.Denied, "Windows denied the removal request."),
            _ => (DeviceRemovalResult.Failed,
                $"The removal request failed (CM error {status.ToString(CultureInfo.InvariantCulture)}).")
        };
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Locate_DevNodeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Request_Device_EjectW")]
    private static partial int CM_Request_Device_EjectW(
        uint devInst,
        ref int vetoType,
        ref ushort vetoName,
        uint vetoNameLength,
        uint flags);
}
