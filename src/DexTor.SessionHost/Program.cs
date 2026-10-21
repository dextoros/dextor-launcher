namespace DexTor.SessionHost;

/// <summary>
/// The session host executable. Running it with no arguments does nothing, as before.
/// </summary>
/// <remarks>
/// The only mode it has is the read-only USB diagnostic. Entering it starts no session,
/// no recovery, no provisioning and no virtual machine: the diagnostic is reached here,
/// before any of that exists.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return 0;
        }

        if (args[0] == UsbDiagnosticCommand.ModeArgument)
        {
            return UsbDiagnosticCommand.Run(args[1..], Console.Out);
        }

        Console.Error.WriteLine($"Unrecognized mode: {args[0]}");
        Console.Error.WriteLine(UsbDiagnosticCommand.Usage);
        return UsbDiagnosticExitCode.Usage;
    }
}
