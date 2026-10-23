using System.Globalization;
using System.Text.Json;
using DexTor.Qemu;
using FluentAssertions;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Installs the <c>DexTor.FakeQemu</c> executable into a temporary runtime directory
/// under QEMU's real name, so the production launch path runs against a real Windows
/// process it created itself.
/// </summary>
/// <remarks>
/// The point is that nothing about starting the process is substituted. The session host
/// resolves this file through <c>VmPrelaunchGate.ValidateRuntime</c>, builds the real
/// argument list through <c>QemuLaunchPlanBuilder</c>, and hands both to
/// <c>QemuProcessLauncher</c>. Behaviour is configured through a file beside the
/// executable because the command line is fixed by the plan builder and must stay that
/// way, and because per-process environment changes are unsafe across parallel tests.
/// </remarks>
internal sealed class QemuStandIn
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _runtimeDirectory;

    private QemuStandIn(string runtimeDirectory, string reportPath, string startedPath, string shutdownPath, string descendantPidPath)
    {
        _runtimeDirectory = runtimeDirectory;
        ReportPath = reportPath;
        StartedPath = startedPath;
        ShutdownPath = shutdownPath;
        DescendantPidPath = descendantPidPath;
    }

    /// <summary>Where the stand-in records its argv, working directory and environment.</summary>
    public string ReportPath { get; }

    /// <summary>Created by the stand-in as soon as it is running.</summary>
    public string StartedPath { get; }

    /// <summary>Created once the stand-in has answered the whole QMP handshake.</summary>
    public string HandshakePath => Path.Combine(_runtimeDirectory, "handshake");

    /// <summary>Creating this file asks the stand-in to exit.</summary>
    public string ShutdownPath { get; }

    /// <summary>Where the stand-in records a descendant it spawned.</summary>
    public string DescendantPidPath { get; }

    public string ExecutablePath => Path.Combine(_runtimeDirectory, "qemu-system-x86_64.exe");

    /// <summary>
    /// Copies the stand-in into <paramref name="runtimeDirectory"/> and writes the runtime
    /// manifest the prelaunch gate requires.
    /// </summary>
    public static QemuStandIn Install(string runtimeDirectory, string manifestPath)
    {
        Directory.CreateDirectory(runtimeDirectory);
        var source = Path.Combine(AppContext.BaseDirectory, "fake-qemu");
        Directory.Exists(source).Should().BeTrue(
            "DexTor.FakeQemu builds into the test output as fake-qemu\\; check the project reference");

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var name = Path.GetFileName(file);

            // The .NET apphost records the managed assembly it starts, so renaming the
            // executable does not change what it runs. That is what lets the stand-in be
            // resolved by the same name the real runtime uses.
            var destination = string.Equals(name, "DexTor.FakeQemu.exe", StringComparison.Ordinal)
                ? Path.Combine(runtimeDirectory, "qemu-system-x86_64.exe")
                : Path.Combine(runtimeDirectory, name);
            File.Copy(file, destination, true);
        }

        File.WriteAllText(manifestPath, """{"runtime":{"files":["qemu-system-x86_64.exe"]}}""");

        var standIn = new QemuStandIn(
            runtimeDirectory,
            Path.Combine(runtimeDirectory, "report.json"),
            Path.Combine(runtimeDirectory, "started"),
            Path.Combine(runtimeDirectory, "shutdown"),
            Path.Combine(runtimeDirectory, "descendant.pid"));
        standIn.Configure();
        return standIn;
    }

    /// <summary>
    /// Writes the stand-in's behaviour file. Defaults answer the QMP handshake and then
    /// stay alive until asked to stop.
    /// </summary>
    public void Configure(
        string mode = "qmp",
        int exitCode = 0,
        bool spawnDescendant = false,
        bool honorPowerdown = true,
        bool honorQuit = true,
        bool emitShutdownEvent = true,
        bool emitShutdownEventBeforeAck = false,
        bool shutdownEventGuest = true,
        string shutdownEventName = "SHUTDOWN",
        string shutdownEventReason = "guest-shutdown",
        int exitDelayMilliseconds = 0,
        int maximumLifetimeSeconds = 120)
    {
        File.WriteAllText(
            Path.Combine(_runtimeDirectory, "qemu-standin.json"),
            JsonSerializer.Serialize(new
            {
                mode,
                exitCode,
                reportPath = ReportPath,
                startedPath = StartedPath,
                handshakePath = HandshakePath,
                shutdownPath = ShutdownPath,
                guestActionPath = GuestActionPath,
                descendantPidPath = DescendantPidPath,
                spawnDescendant,
                honorPowerdown,
                honorQuit,
                emitShutdownEvent,
                emitShutdownEventBeforeAck,
                shutdownEventGuest,
                shutdownEventName,
                shutdownEventReason,
                exitDelayMilliseconds,
                maximumLifetimeSeconds
            }));
    }

    /// <summary>The QEMU argument list the plan builder produces for this stand-in.</summary>
    public QemuLaunchPlan BuildPlan(string systemImagePath, string dataImagePath) =>
        QemuLaunchPlanBuilder.Build(
            ExecutablePath,
            new ResolvedSystemImage(systemImagePath, SystemImageLocation.UsbFallback),
            new DexTor.Contracts.SessionStartRequest(
                Guid.NewGuid(),
                systemImagePath,
                systemImagePath,
                dataImagePath,
                new string('a', 64),
                4096,
                2));

    /// <summary>
    /// Replaces the stand-in with a file Windows cannot execute, so the production
    /// launcher fails at <c>CreateProcessW</c>. The runtime still passes validation, so
    /// the failure happens where a real broken QEMU binary would cause it.
    /// </summary>
    public void BreakExecutable() => File.WriteAllText(ExecutablePath, "not a portable executable");

    public void RequestShutdown() => File.WriteAllText(ShutdownPath, "stop");

    /// <summary>Asked of the stand-in: the guest acts from inside - "reset" or "shutdown".</summary>
    public string GuestActionPath => Path.Combine(_runtimeDirectory, "guest-action");

    /// <summary>The guest restarts from inside: QEMU reports RESET and runs on.</summary>
    public void RestartGuest() => RequestGuestAction("reset");

    /// <summary>The guest powers off from inside: QEMU reports SHUTDOWN guest-shutdown and exits.</summary>
    public void ShutDownGuest() => RequestGuestAction("shutdown");

    /// <summary>Written then renamed, so the stand-in never reads half a word.</summary>
    private void RequestGuestAction(string action)
    {
        File.WriteAllText(GuestActionPath + ".partial", action);
        File.Move(GuestActionPath + ".partial", GuestActionPath, true);
    }

    /// <summary>True once the stand-in has taken the last requested guest action.</summary>
    public bool GuestActionTaken => !File.Exists(GuestActionPath);

    /// <summary>The stand-in's own process id, taken from the report it writes.</summary>
    public int ReadProcessId() => ReadReport().ProcessId;

    public int ReadDescendantProcessId() =>
        int.Parse(File.ReadAllText(DescendantPidPath).Trim(), CultureInfo.InvariantCulture);

    public StandInReport ReadReport() =>
        JsonSerializer.Deserialize<StandInReport>(File.ReadAllText(ReportPath), JsonOptions)!;

    /// <summary>Polls a condition to a deadline. No sleeps stand in for a barrier.</summary>
    public static bool WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }

    internal sealed record StandInReport(
        int ProcessId,
        string Executable,
        string[] Arguments,
        string WorkingDirectory,
        string? EnvironmentMarker);
}
