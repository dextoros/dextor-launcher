using System.Globalization;
using DexTor.Contracts;

namespace DexTor.SessionHost;

/// <summary>
/// One plain-text log per VM start in <c>%LOCALAPPDATA%\DexTor\Sessions</c>: the launcher
/// version, the computer, the QEMU command line, what QEMU said and how the session ended.
/// </summary>
/// <remarks>
/// A tester's "it hangs on 'Display output is not active'" came with creation journals
/// only: nothing recorded how the VM had been started, so the CPU profile behind the hang
/// had to be guessed. Logging never fails a start; the last twenty logs are kept.
/// </remarks>
public static class VmSessionLog
{
    private const int Kept = 20;

    public static string Directory => Path.Combine(DexTorStorage.Root, "Sessions");

    public static string PathFor(Guid sessionId) => Path.Combine(Directory, $"{sessionId:N}.log");

    public static void Started(Guid sessionId, string executable, IReadOnlyList<string> arguments, string profile, int memoryMiB, int processors)
    {
        Write(sessionId,
            $"launcher {typeof(VmSessionLog).Assembly.GetName().Version} channel {DexTorBuildInfo.Channel}",
            $"windows {Environment.OSVersion.Version} cpu {Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")} threads {Environment.ProcessorCount}",
            $"profile {profile} memory {memoryMiB} MiB processors {processors}",
            $"qemu {executable}",
            "args " + string.Join(' ', arguments.Select(Quote)));
        Prune();
    }

    public static void Note(Guid sessionId, string line) => Write(sessionId, line);

    private static void Write(Guid sessionId, params string[] lines)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var stamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
            File.AppendAllLines(PathFor(sessionId), lines.Select(line => $"{stamp} {line.ReplaceLineEndings(" | ")}"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must not stop DexTor OS from starting.
        }
    }

    private static void Prune()
    {
        try
        {
            foreach (var old in new DirectoryInfo(Directory).GetFiles("*.log")
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(Kept))
            {
                old.Delete();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Quote(string argument) => argument.Contains(' ', StringComparison.Ordinal) ? $"\"{argument}\"" : argument;
}
