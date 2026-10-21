namespace DexTor.Launcher;

/// <summary>Closes File Explorer windows that show a drive about to be erased.</summary>
/// <remarks>
/// An Explorer window left open on the stick - AutoPlay opens one when it is plugged in -
/// loses its drive the moment the stick is cleared, and Windows answers with
/// "F:\ is not accessible". Closing those windows first is what a person would do by hand.
/// Only windows whose location is on that drive are touched.
/// </remarks>
internal static class ExplorerWindows
{
    internal static int CloseOn(string? driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot) || driveRoot.Length < 2 || driveRoot[1] != ':') return 0;
        var prefix = driveRoot[..2];
        var closed = 0;
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null) return 0;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic windows = shell.Windows();
            var count = (int)windows.Count;
            for (var index = count - 1; index >= 0; index--)
            {
                dynamic? window = windows.Item(index);
                if (window is null) continue;
                string location;
                try
                {
                    location = (string)window.Document.Folder.Self.Path;
                }
                catch (Exception exception) when (exception is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException)
                {
                    continue;
                }

                if (IsOnDrive(location, prefix))
                {
                    window.Quit();
                    closed++;
                }
            }
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
        {
            // Explorer not answering: nothing to close, and nothing here is required.
        }

        return closed;
    }

    internal static bool IsOnDrive(string? location, string drivePrefix) =>
        !string.IsNullOrWhiteSpace(location)
        && location.StartsWith(drivePrefix, StringComparison.OrdinalIgnoreCase)
        && (location.Length == 2 || location[2] == '\\');
}
