using System;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DexTor.Application;

namespace DexTor.Platform.Windows;

public interface IHostPreflightEvaluator
{
    Task<PreflightReport> EvaluateAsync(CancellationToken cancellationToken = default);
}

public class HostPreflightEvaluator : IHostPreflightEvaluator
{
    private const string Win11DownloadLink = "https://www.microsoft.com/en-us/software-download/windows11";
    private const string DotNetDownloadLink = PreflightGuide.DotNetDownloadUrl;
    private const string LiveImageSourceIndexUrl = "https://dextoros.com/public/lastversion.link";
    public const string WhpxGuideLink = "https://learn.microsoft.com/en-us/dotnet/maui/android/emulator/troubleshooting?view=net-maui-10.0";
    private const int LiveImageCheckTimeoutSeconds = 6;

    public Task<PreflightReport> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var requirements = new List<PreflightRequirement>
        {
            CheckOs(),
            CheckArchitecture(),
            CheckRuntime(),
            CheckMemory(),
            CheckDiskSpace(),
            CheckLiveImageSource(),
            CheckWhpx()
        };

        var canStart = requirements.TrueForAll(r => r.State != PreflightRequirementState.Blocker);
        return Task.FromResult(new PreflightReport(canStart, requirements));
    }

    /// <summary>The virtual machine is given 4 GiB; below this the host has none left for itself.</summary>
    public const long MinimumMemoryBytes = 6L * 1024 * 1024 * 1024;
    public const long RecommendedMemoryBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>The image (about 1.7 GB), its partial download and the QEMU runtime (about 1.2 GB).</summary>
    public const long MinimumFreeBytes = 4L * 1024 * 1024 * 1024;
    public const long RecommendedFreeBytes = 8L * 1024 * 1024 * 1024;

    protected virtual PreflightRequirement CheckOs()
    {
        var isWindows11 = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

        return isWindows11
            ? new PreflightRequirement(
                PreflightRequirementKind.OperatingSystem,
                PreflightRequirementState.Ok,
                "preflight.os.title",
                "preflight.os.ok",
                DescriptionArgs: DescribeWindows())
            : new PreflightRequirement(
                PreflightRequirementKind.OperatingSystem,
                PreflightRequirementState.Blocker,
                "preflight.os.title",
                "preflight.os.blocker",
                ActionTextKey: "preflight.os.action",
                ActionUrl: Win11DownloadLink);
    }

    /// <summary>"Windows 11 Pro", "24H2", "26200" - what the operator knows their Windows by.</summary>
    internal static Dictionary<string, string> DescribeWindows()
    {
        var build = Environment.OSVersion.Version.Build;
        string? product = null;
        string? release = null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            product = key?.GetValue("ProductName") as string;
            release = key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
        }

        // Windows 11 still calls itself "Windows 10" in ProductName; the build tells them apart.
        product = string.IsNullOrWhiteSpace(product) ? "Windows" : product;
        if (build >= 22000) product = product.Replace("Windows 10", "Windows 11", StringComparison.Ordinal);
        return new Dictionary<string, string>
        {
            ["product"] = product,
            ["release"] = release ?? string.Empty,
            ["build"] = build.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    protected virtual long? TotalMemoryBytes()
    {
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total > 0 ? total : null;
    }

    protected virtual (string Drive, long FreeBytes)? CacheDriveFreeSpace()
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            if (string.IsNullOrWhiteSpace(root)) return null;
            var drive = new System.IO.DriveInfo(root);
            return (drive.Name.TrimEnd(System.IO.Path.DirectorySeparatorChar), drive.AvailableFreeSpace);
        }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    protected virtual PreflightRequirement CheckMemory()
    {
        var total = TotalMemoryBytes();
        var args = new Dictionary<string, string> { ["bytes"] = (total ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture) };
        var state = total is null || total >= RecommendedMemoryBytes
            ? PreflightRequirementState.Ok
            : total >= MinimumMemoryBytes ? PreflightRequirementState.Warning : PreflightRequirementState.Blocker;
        return new PreflightRequirement(
            PreflightRequirementKind.Memory,
            state,
            "preflight.memory.title",
            state switch
            {
                PreflightRequirementState.Ok => "preflight.memory.ok",
                PreflightRequirementState.Warning => "preflight.memory.warning",
                _ => "preflight.memory.blocker"
            },
            DescriptionArgs: total is null ? null : args);
    }

    protected virtual PreflightRequirement CheckDiskSpace()
    {
        var space = CacheDriveFreeSpace();
        var state = space is not { } known || known.FreeBytes >= RecommendedFreeBytes
            ? PreflightRequirementState.Ok
            : known.FreeBytes >= MinimumFreeBytes ? PreflightRequirementState.Warning : PreflightRequirementState.Blocker;
        return new PreflightRequirement(
            PreflightRequirementKind.DiskSpace,
            state,
            "preflight.disk.title",
            state switch
            {
                PreflightRequirementState.Ok => "preflight.disk.ok",
                PreflightRequirementState.Warning => "preflight.disk.warning",
                _ => "preflight.disk.blocker"
            },
            DescriptionArgs: space is { } value
                ? new Dictionary<string, string>
                {
                    ["drive"] = value.Drive,
                    ["bytes"] = value.FreeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }
                : null);
    }

    protected virtual PreflightRequirement CheckArchitecture()
    {
        return Environment.Is64BitProcess && Environment.Is64BitOperatingSystem
            ? new PreflightRequirement(
                PreflightRequirementKind.ProcessorArchitecture,
                PreflightRequirementState.Ok,
                "preflight.arch.title",
                "preflight.arch.ok")
            : new PreflightRequirement(
                PreflightRequirementKind.ProcessorArchitecture,
                PreflightRequirementState.Blocker,
                "preflight.arch.title",
                "preflight.arch.blocker");
    }

    protected virtual PreflightRequirement CheckRuntime()
    {
        var version = Environment.Version;
        var isRuntimeSupported = version.Major >= 10;
        var args = new Dictionary<string, string>
        {
            ["version"] = version.ToString()
        };

        return isRuntimeSupported
            ? new PreflightRequirement(
                PreflightRequirementKind.Runtime,
                PreflightRequirementState.Ok,
                "preflight.runtime.title",
                "preflight.runtime.ok",
                DescriptionArgs: args)
            : new PreflightRequirement(
                PreflightRequirementKind.Runtime,
                PreflightRequirementState.Blocker,
                "preflight.runtime.title",
                "preflight.runtime.blocker",
                ActionTextKey: "preflight.runtime.blocker.action",
                ActionUrl: DotNetDownloadLink);
    }

    protected virtual PreflightRequirement CheckLiveImageSource()
    {
        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(LiveImageCheckTimeoutSeconds)
            };

            var indexText = client.GetStringAsync(LiveImageSourceIndexUrl).GetAwaiter().GetResult();
            var resolvedUrl = indexText
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

            if (string.IsNullOrWhiteSpace(resolvedUrl))
            {
                return new PreflightRequirement(
                    PreflightRequirementKind.LiveImage,
                    PreflightRequirementState.Warning,
                    "preflight.live_image.title",
                    "preflight.live_image.warning",
                    SolutionTextKey: "launcher.preflight.live_image.solution",
                    InfoTextKey: "launcher.preflight.live_image.unavailable");
            }

            if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out var isoUri) ||
                (isoUri.Scheme != Uri.UriSchemeHttp && isoUri.Scheme != Uri.UriSchemeHttps))
            {
                return new PreflightRequirement(
                    PreflightRequirementKind.LiveImage,
                    PreflightRequirementState.Warning,
                    "preflight.live_image.title",
                    "preflight.live_image.warning",
                    SolutionTextKey: "launcher.preflight.live_image.solution",
                    InfoTextKey: "launcher.preflight.live_image.unavailable");
            }

            if (!IsResourceAvailable(client, isoUri))
            {
                return new PreflightRequirement(
                    PreflightRequirementKind.LiveImage,
                    PreflightRequirementState.Warning,
                    "preflight.live_image.title",
                    "preflight.live_image.warning",
                    SolutionTextKey: "launcher.preflight.live_image.solution",
                    InfoTextKey: "launcher.preflight.live_image.unavailable");
            }

                return new PreflightRequirement(
                    PreflightRequirementKind.LiveImage,
                    PreflightRequirementState.Ok,
                    "preflight.live_image.title",
                    "preflight.live_image.ok",
                    InfoTextKey: "launcher.preflight.live_image.resolved_url",
                InfoTextArgs: new Dictionary<string, string>
                {
                    ["url"] = resolvedUrl
                });
        }
        catch
        {
                return new PreflightRequirement(
                    PreflightRequirementKind.LiveImage,
                    PreflightRequirementState.Warning,
                    "preflight.live_image.title",
                    "preflight.live_image.warning",
                    SolutionTextKey: "launcher.preflight.live_image.solution",
                    InfoTextKey: "launcher.preflight.live_image.unavailable");
        }
    }

    private static bool IsResourceAvailable(HttpClient client, Uri resource)
    {
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, resource);
            using var headResponse = client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
            if (headResponse.IsSuccessStatusCode)
            {
                return true;
            }

            if (headResponse.StatusCode == HttpStatusCode.MethodNotAllowed || headResponse.StatusCode == HttpStatusCode.NotFound)
            {
                using var getResponse = client.GetAsync(resource, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                return getResponse.IsSuccessStatusCode;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    protected virtual PreflightRequirement CheckWhpx()
    {
        var enabled = new WindowsHostProbe().IsWhpxEnabledAsync().GetAwaiter().GetResult();

        return enabled == true
            ? new PreflightRequirement(
                PreflightRequirementKind.HostVirtualization,
                PreflightRequirementState.Ok,
                "preflight.virtualization.title",
                "preflight.virtualization.ok")
            : enabled == false
                ? new PreflightRequirement(
                    PreflightRequirementKind.HostVirtualization,
                    PreflightRequirementState.Warning,
                    "preflight.virtualization.title",
                    "preflight.virtualization.warning",
                    ActionTextKey: "launcher.primary_action_prepare",
                    ActionUrl: WhpxGuideLink)
                : new PreflightRequirement(
                    PreflightRequirementKind.HostVirtualization,
                    PreflightRequirementState.Warning,
                    "preflight.virtualization.title",
                    "preflight.virtualization.unknown",
                    ActionTextKey: "launcher.primary_action_prepare",
                    ActionUrl: WhpxGuideLink);
    }

}
