using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DexTor.SessionHost;

/// <summary>Whether Windows has a sound output QEMU's DirectSound backend can open.</summary>
/// <remarks>
/// On a computer without one - a server, a remote desktop without audio, a machine whose
/// sound driver is missing - QEMU cannot open an audio backend
/// and DexTor OS would not start. It then gets a silent card.
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class HostAudio
{
    public static bool HasOutput()
    {
        try
        {
            return waveOutGetNumDevs() > 0;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [LibraryImport("winmm.dll")]
    private static partial uint waveOutGetNumDevs();
}
