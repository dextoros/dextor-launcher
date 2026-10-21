using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace DexTor.FakeQemu;

/// <summary>
/// A real Windows executable that stands in for QEMU, so tests can drive the production
/// launch path end to end instead of substituting a process the launcher never created.
/// </summary>
/// <remarks>
/// Behaviour comes from <c>qemu-standin.json</c> beside the executable rather than from
/// the command line or the environment: the command line is fixed by
/// <c>QemuLaunchPlanBuilder</c> and must stay untouched, and per-process environment
/// changes are not safe while xunit runs classes in parallel. The file is written by the
/// test into its own temporary runtime directory, so every test configures exactly its
/// own stand-in.
/// </remarks>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static int Main(string[] args)
    {
        var configuration = Configuration.Load();
        Report(configuration, args);

        if (configuration.SpawnDescendant)
        {
            SpawnDescendant(configuration);
        }

        if (string.Equals(configuration.Mode, "exit", StringComparison.Ordinal))
        {
            return configuration.ExitCode;
        }

        // Only the greeting is unsolicited. Everything else is a reply carrying back the
        // id its command was sent with, exactly as QEMU answers, so the launcher's
        // command correlation is genuinely exercised rather than bypassed.
        Console.Out.WriteLine("""{"QMP":{"version":{},"capabilities":[]}}""");
        Console.Out.Flush();

        // A barrier the test can wait on, so nothing needs to guess how long start takes.
        Announce(configuration.StartedPath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

        return RunUntilStopped(configuration);
    }

    private static int RunUntilStopped(Configuration configuration)
    {
        // One reader, on a thread of its own. Polling stdin with overlapping reads loses
        // lines: a read that the poller gave up on still consumes the next line when it
        // finally completes, and the command that asked for shutdown disappears.
        using var stopRequested = new ManualResetEventSlim(false);
        var announced = false;
        var reader = new Thread(() =>
        {
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                if (!TryReadCommand(line, out var command, out var id))
                {
                    continue;
                }

                var stop = Answer(configuration, command, id);

                // A second barrier, set once the handshake has actually completed, for
                // tests that need the session live rather than merely started.
                if (!announced && string.Equals(command, "query-status", StringComparison.Ordinal))
                {
                    announced = true;
                    Announce(configuration.HandshakePath, Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                }

                if (stop)
                {
                    stopRequested.Set();
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "fake-qemu qmp reader"
        };
        reader.Start();

        var deadline = DateTime.UtcNow.AddSeconds(configuration.MaximumLifetimeSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (stopRequested.Wait(TimeSpan.FromMilliseconds(25)))
            {
                if (configuration.ExitDelayMilliseconds > 0)
                {
                    Thread.Sleep(configuration.ExitDelayMilliseconds);
                }

                return configuration.ExitCode;
            }

            if (configuration.ShutdownPath is { Length: > 0 } sentinel && File.Exists(sentinel))
            {
                return configuration.ExitCode;
            }
        }

        // Reaching the cap means the test never asked it to stop; exiting keeps a failing
        // test from leaving a process behind.
        return configuration.ExitCode;
    }

    /// <summary>
    /// Replies to one command and says whether it ends the process.
    /// </summary>
    private static bool Answer(Configuration configuration, string command, string? id)
    {
        switch (command)
        {
            case "qmp_capabilities":
            case "cont":
                Reply(id, "{}");
                return false;

            case "query-version":
                Reply(id, """{"qemu":{"major":9,"minor":0,"micro":0}}""");
                return false;

            case "query-status":
                Reply(id, """{"status":"running","running":true}""");
                return false;

            case "system_powerdown":
                if (configuration.EmitShutdownEventBeforeAck)
                {
                    EmitShutdownEvent(configuration);
                }

                Reply(id, "{}");
                if (!configuration.HonorPowerdown)
                {
                    return false;
                }

                if (!configuration.EmitShutdownEventBeforeAck)
                {
                    EmitShutdownEvent(configuration);
                }

                return true;

            case "quit":
                Reply(id, "{}");
                return configuration.HonorQuit;

            default:
                Reply(id, "{}");
                return false;
        }
    }

    private static void EmitShutdownEvent(Configuration configuration)
    {
        if (!configuration.EmitShutdownEvent)
        {
            return;
        }

        if (string.Equals(configuration.ShutdownEventName, "GUEST_PANICKED", StringComparison.Ordinal))
        {
            Console.Out.WriteLine("""{"event":"GUEST_PANICKED","data":{"action":"pause"}}""");
            Console.Out.Flush();
            return;
        }

        var guest = configuration.ShutdownEventGuest ? "true" : "false";
        var payload = "{\"event\":\"" + configuration.ShutdownEventName
            + "\",\"data\":{\"guest\":" + guest
            + ",\"reason\":\"" + configuration.ShutdownEventReason + "\"}}";
        Console.Out.WriteLine(payload);
        Console.Out.Flush();
    }

    private static void Reply(string? id, string payload)
    {
        Console.Out.WriteLine(id is null
            ? "{\"return\":" + payload + "}"
            : "{\"return\":" + payload + ",\"id\":\"" + id + "\"}");
        Console.Out.Flush();
    }

    private static bool TryReadCommand(string line, out string command, out string? id)
    {
        command = string.Empty;
        id = null;
        try
        {
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("execute", out var execute))
            {
                return false;
            }

            command = execute.GetString() ?? string.Empty;
            id = document.RootElement.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            return command.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void SpawnDescendant(Configuration configuration)
    {
        // Created after this process is already a job member, so the descendant inherits
        // the job and a containment test can prove it did.
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("ping -n 300 127.0.0.1 >nul");
        using var child = Process.Start(startInfo);
        if (child is not null)
        {
            Announce(configuration.DescendantPidPath, child.Id.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void Report(Configuration configuration, string[] args)
    {
        if (configuration.ReportPath is not { Length: > 0 } path)
        {
            return;
        }

        var report = JsonSerializer.Serialize(new
        {
            processId = Environment.ProcessId,
            executable = Environment.ProcessPath,
            arguments = args,
            workingDirectory = Environment.CurrentDirectory,
            environmentMarker = Environment.GetEnvironmentVariable("DEXTOR_TEST_MARKER")
        });
        Announce(path, report);
    }

    /// <summary>
    /// Writes then renames, so a test that polls for the file never reads a partial one.
    /// </summary>
    private static void Announce(string? path, string content)
    {
        if (path is not { Length: > 0 })
        {
            return;
        }

        var temporary = path + ".partial";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }

    private sealed record Configuration
    {
        public string Mode { get; init; } = "qmp";
        public int ExitCode { get; init; }
        public string? ReportPath { get; init; }
        public string? StartedPath { get; init; }
        public string? HandshakePath { get; init; }
        public string? ShutdownPath { get; init; }
        public string? DescendantPidPath { get; init; }
        public bool SpawnDescendant { get; init; }
        public bool HonorPowerdown { get; init; } = true;
        public bool HonorQuit { get; init; } = true;
        public bool EmitShutdownEvent { get; init; } = true;
        public bool EmitShutdownEventBeforeAck { get; init; }
        public bool ShutdownEventGuest { get; init; } = true;
        public string ShutdownEventName { get; init; } = "SHUTDOWN";
        public string ShutdownEventReason { get; init; } = "guest-shutdown";
        public int ExitDelayMilliseconds { get; init; }
        public int MaximumLifetimeSeconds { get; init; } = 120;

        public static Configuration Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "qemu-standin.json");
            if (!File.Exists(path))
            {
                return new Configuration();
            }

            return JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path), JsonOptions)
                ?? new Configuration();
        }
    }
}
