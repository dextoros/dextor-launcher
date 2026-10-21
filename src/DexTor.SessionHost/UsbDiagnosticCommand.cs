using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text;

namespace DexTor.SessionHost;

/// <summary>
/// The exit codes <c>DexTor.SessionHost.exe --diagnose-usb</c> returns.
/// </summary>
/// <remarks>
/// They are part of the operator interface: a wrapper or a log reader is expected to act
/// on them, so they are documented here and in <c>scripts/diagnose-usb.ps1</c> and are not
/// renumbered casually.
/// </remarks>
public static class UsbDiagnosticExitCode
{
    /// <summary>The resolver accepted the named storage as a removal target.</summary>
    /// <remarks>
    /// This says the resolver would name that device. It does not say Windows will approve
    /// the removal, and it says nothing at all about guest storage being consistent.
    /// </remarks>
    public const int Accepted = 0;

    /// <summary>The resolver refused. The report carries the reason.</summary>
    public const int Refused = 10;

    /// <summary>The named image is not there — never was, or disappeared.</summary>
    public const int TargetMissing = 11;

    /// <summary>Windows denied the read this diagnostic needs.</summary>
    public const int TargetDenied = 12;

    /// <summary>
    /// The storage could not be read at all, so nothing was established either way.
    /// </summary>
    /// <remarks>
    /// Reserved for the queries this command makes itself: the volume behind the path, the
    /// volume's own properties, the system disk and the present-disk enumeration. A device
    /// node query that fails deeper in the chain comes back as <see cref="Refused"/> with a
    /// reason naming what was unknown — the resolver refuses on an unknown, and that
    /// refusal is reported as a refusal rather than reclassified here.
    /// </remarks>
    public const int TargetUnknown = 13;

    /// <summary>The arguments were not usable.</summary>
    public const int Usage = 64;

    /// <summary>The report destination was not verified host-side storage.</summary>
    public const int OutputRejected = 65;

    /// <summary>This is a Windows diagnostic and the host is not Windows.</summary>
    public const int Unsupported = 66;

    /// <summary>Something failed that none of the above describes.</summary>
    public const int Failure = 70;
}

/// <summary>
/// The runnable form of <see cref="UsbRemovalDiagnostics"/>: an operator names one image,
/// names one host-side report file, and gets back what the production resolver decides.
/// </summary>
/// <remarks>
/// It inspects and reports. There is no image creation, journal write, ownership recovery,
/// repair, flush, dismount, eject, format or guest execution here, and no session is
/// started: this runs from <see cref="Program"/> before any session state exists.
/// <para>
/// The device is always named by the caller. Nothing enumerates removable drives to pick
/// one, because a diagnostic that chooses a device can choose the wrong one.
/// </para>
/// <para>
/// The report is written to the host, never to the inspected device. The guard compares
/// volume identity rather than spelling, so a drive letter, a mounted folder or any other
/// alias of the same volume is refused the same way.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class UsbDiagnosticCommand
{
    internal const string ModeArgument = "--diagnose-usb";

    private const string AcceptanceLimit =
        "Acceptance means the resolver would name this device. It is not a promise that Windows will "
        + "approve the removal, and it says nothing about whether guest storage is consistent.";

    public static string Usage =>
        "DexTor USB removal diagnostic (read-only).\n"
        + "\n"
        + "  DexTor.SessionHost.exe --diagnose-usb --data-image <path> --output <path> [--force]\n"
        + "\n"
        + "  --data-image <path>  The image to inspect, for example E:\\DEXTOR\\storage\\data.img.\n"
        + "                       Required. No device is ever chosen for you.\n"
        + "  --output <path>      Where to write the report. Must be host storage, on a different\n"
        + "                       volume from the image, in a directory that already exists.\n"
        + "  --force              Overwrite an existing report file.\n"
        + "\n"
        + "Exit codes: 0 accepted, 10 refused, 11 target missing, 12 access denied,\n"
        + "            13 storage unreadable, 64 usage, 65 output rejected,\n"
        + "            66 unsupported host, 70 unexpected failure.\n"
        + "\n"
        + AcceptanceLimit;

    /// <summary>
    /// Runs the diagnostic.
    /// </summary>
    /// <param name="arguments">Everything after <c>--diagnose-usb</c>.</param>
    /// <param name="console">Where the short summary goes. The report goes to a file.</param>
    /// <param name="topology">
    /// The device topology to ask. Production passes none and the real Windows topology is
    /// used; tests pass a recorded one so the command can be exercised without hardware.
    /// </param>
    public static int Run(string[] arguments, TextWriter console, INativeDeviceTopology? topology = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(console);

        if (!OperatingSystem.IsWindows())
        {
            console.WriteLine("This diagnostic reads Windows storage and device topology; the host is not Windows.");
            return UsbDiagnosticExitCode.Unsupported;
        }

        if (!TryParse(arguments, console, out var request))
        {
            return UsbDiagnosticExitCode.Usage;
        }

        try
        {
            return Execute(request, console, topology ?? new WindowsDeviceTopology());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The diagnostic could not complete: {exception.Message}"));
            return UsbDiagnosticExitCode.Failure;
        }
    }

    private sealed record Request(string DataImagePath, string OutputPath, bool Force);

    private static bool TryParse(string[] arguments, TextWriter console, out Request request)
    {
        request = null!;
        string? image = null;
        string? output = null;
        var force = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            switch (argument)
            {
                case "--data-image":
                case "--output":
                    // The value is the next argument, whole. Spaces and non-ASCII
                    // characters are ordinary content of a path, never separators.
                    if (index + 1 >= arguments.Length)
                    {
                        console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{argument} needs a value."));
                        console.WriteLine(Usage);
                        return false;
                    }

                    if (argument == "--data-image")
                    {
                        image = arguments[index + 1];
                    }
                    else
                    {
                        output = arguments[index + 1];
                    }

                    index++;
                    break;

                case "--force":
                    force = true;
                    break;

                case "--help":
                case "-h":
                case "-?":
                    console.WriteLine(Usage);
                    return false;

                default:
                    console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Unrecognized argument: {argument}"));
                    console.WriteLine(Usage);
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(image))
        {
            console.WriteLine("--data-image is required; this diagnostic will not pick a device.");
            console.WriteLine(Usage);
            return false;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            console.WriteLine("--output is required; the report is never left on the inspected device.");
            console.WriteLine(Usage);
            return false;
        }

        request = new Request(image, output, force);
        return true;
    }

    private static int Execute(Request request, TextWriter console, INativeDeviceTopology topology)
    {
        var imageVolume = topology.VolumeGuidPathFor(request.DataImagePath);
        if (!TryAcceptOutput(request, imageVolume, console, topology, out var outputPath))
        {
            return UsbDiagnosticExitCode.OutputRejected;
        }

        var report = new StringBuilder();
        AppendHeader(report, request, outputPath);
        var access = AppendImageAccess(report, request.DataImagePath);
        report.AppendLine();
        report.Append(UsbRemovalDiagnostics.Describe(request.DataImagePath, topology));

        var code = Classify(access, imageVolume, topology, request.DataImagePath);
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Result           : {Name(code)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Exit code        : {code.ToString(CultureInfo.InvariantCulture)}");
        report.AppendLine();
        report.AppendLine(AcceptanceLimit);

        File.WriteAllText(outputPath, report.ToString(), new UTF8Encoding(false));

        console.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"Result: {Name(code)} (exit {code.ToString(CultureInfo.InvariantCulture)})"));
        console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Report: {outputPath}"));
        return code;
    }

    /// <summary>
    /// Verifies the report destination before anything is written.
    /// </summary>
    /// <remarks>
    /// The destination must be on a volume Windows can name, that volume must not be the
    /// one holding the image, and its directory must already exist — nothing is created on
    /// the way to a report. When the image's own volume cannot be resolved there is nothing
    /// to compare against, so the destination is checked against the image's directory
    /// instead and anything underneath it is refused.
    /// </remarks>
    private static bool TryAcceptOutput(
        Request request,
        NativeAnswer<string> imageVolume,
        TextWriter console,
        INativeDeviceTopology topology,
        out string outputPath)
    {
        outputPath = string.Empty;
        string full;
        string? directory;
        try
        {
            full = Path.GetFullPath(request.OutputPath);
            directory = Path.GetDirectoryName(full);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"The report path is not usable: {exception.Message}"));
            return false;
        }

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            console.WriteLine("The report directory must already exist; this diagnostic does not create directories.");
            return false;
        }

        if (Directory.Exists(full))
        {
            console.WriteLine("The report path names a directory.");
            return false;
        }

        if (File.Exists(full) && !request.Force)
        {
            console.WriteLine("A report already exists at that path. Choose another name, or pass --force.");
            return false;
        }

        var outputVolume = topology.VolumeGuidPathFor(full);
        if (!outputVolume.Succeeded || outputVolume.Value is not { } destination)
        {
            console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"The report destination is not on a verified local volume ({outputVolume.Error})."));
            return false;
        }

        if (imageVolume.Succeeded && imageVolume.Value is { } inspected)
        {
            if (string.Equals(inspected.TrimEnd('\\'), destination.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                console.WriteLine(
                    "The report would land on the volume being inspected. Write it to host storage instead.");
                return false;
            }
        }
        else
        {
            var imageDirectory = Path.GetDirectoryName(Path.GetFullPath(request.DataImagePath));
            if (!string.IsNullOrEmpty(imageDirectory)
                && full.StartsWith(
                    imageDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                console.WriteLine(
                    "The volume behind the image is unknown and the report would land beside it, so it was refused.");
                return false;
            }
        }

        outputPath = full;
        return true;
    }

    private static void AppendHeader(StringBuilder report, Request request, string outputPath)
    {
        report.AppendLine("== Diagnostic run ==");
        report.AppendLine(CultureInfo.InvariantCulture, $"Build commit     : {BuildCommit}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Command          : {ModeArgument}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Target image     : {request.DataImagePath}");
        report.AppendLine(CultureInfo.InvariantCulture, $"Report path      : {outputPath}");
        report.AppendLine();
    }

    /// <summary>
    /// What the image itself allows, opened with the least access an inspection needs.
    /// </summary>
    /// <remarks>
    /// Read access only, and shared, so a running session is never disturbed. The handle is
    /// closed before anything else happens; nothing here writes, extends or creates a file.
    /// </remarks>
    private static ImageAccess AppendImageAccess(StringBuilder report, string dataImagePath)
    {
        report.AppendLine("== Selected image ==");
        try
        {
            using var stream = new FileStream(
                dataImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            report.AppendLine("Readable         : yes (read-only, shared)");
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"Length (bytes)   : {stream.Length.ToString(CultureInfo.InvariantCulture)}");
            return ImageAccess.Readable;
        }
        catch (FileNotFoundException)
        {
            report.AppendLine("Readable         : no — the file is not there (missing, or it disappeared).");
            return ImageAccess.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            report.AppendLine("Readable         : no — the directory is not there (missing, or it disappeared).");
            return ImageAccess.Missing;
        }
        catch (UnauthorizedAccessException exception)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Readable         : no — access denied ({exception.Message})");
            return ImageAccess.Denied;
        }
        catch (IOException exception)
        {
            // A session holding the image exclusively is an ordinary state, not a failure of
            // the diagnostic: the resolver does not need the file open.
            report.AppendLine(
                CultureInfo.InvariantCulture,
                $"Readable         : in use by another process ({exception.Message})");
            return ImageAccess.InUse;
        }
    }

    private enum ImageAccess
    {
        Readable,
        Missing,
        Denied,
        InUse,
    }

    private static int Classify(
        ImageAccess access,
        NativeAnswer<string> imageVolume,
        INativeDeviceTopology topology,
        string dataImagePath)
    {
        if (access == ImageAccess.Missing)
        {
            return UsbDiagnosticExitCode.TargetMissing;
        }

        if (access == ImageAccess.Denied)
        {
            return UsbDiagnosticExitCode.TargetDenied;
        }

        if (!imageVolume.Succeeded || imageVolume.Value is not { } volume)
        {
            return UsbDiagnosticExitCode.TargetUnknown;
        }

        var facts = topology.QueryVolume(volume);
        if (!facts.DiskNumbers.Succeeded
            || !facts.RemovableMedia.Succeeded
            || !facts.BusType.Succeeded
            || !topology.SystemDiskNumber().Succeeded
            || !topology.EnumeratePresentDisks().Succeeded)
        {
            return UsbDiagnosticExitCode.TargetUnknown;
        }

        return new RemovableTargetResolver(topology).Resolve(dataImagePath).Resolved
            ? UsbDiagnosticExitCode.Accepted
            : UsbDiagnosticExitCode.Refused;
    }

    private static string Name(int code) => code switch
    {
        UsbDiagnosticExitCode.Accepted => "accepted",
        UsbDiagnosticExitCode.Refused => "refused",
        UsbDiagnosticExitCode.TargetMissing => "target missing",
        UsbDiagnosticExitCode.TargetDenied => "access denied",
        UsbDiagnosticExitCode.TargetUnknown => "unknown — the storage could not be read",
        _ => "failed",
    };

    /// <summary>
    /// The commit this binary was built from, stamped at build time.
    /// </summary>
    /// <remarks>
    /// A report that cannot say which build produced it cannot be matched to the code that
    /// made the decision, so an unstamped build says that plainly instead of staying silent.
    /// </remarks>
    internal static string BuildCommit =>
        typeof(UsbDiagnosticCommand).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildCommit")?.Value
        ?? "unknown (this build carries no commit stamp)";
}
