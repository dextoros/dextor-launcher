using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace DexTor.Launcher;

// Built-in COM interop on purpose: the Running Object Table and IMoniker are only
// available as the classic ComTypes interfaces, and the canceller must reach Explorer as
// an ordinary COM object. Source-generated COM would need all of them redeclared.
#pragma warning disable SYSLIB1054, SYSLIB1096

/// <summary>
/// Declines Windows AutoPlay for as long as it is alive, whichever window is in front.
/// </summary>
/// <remarks>
/// While a stick is being rewritten, Windows briefly sees volumes that are gone a moment
/// later - the whole-disk raw volume right after <c>clean</c>, under the stick's old
/// letter - and AutoPlay reacted to each: "You need to format the disk in drive F:",
/// or "F:\ is not accessible" when Explorer reached for a volume that had already left.
/// The wizard already answers the <c>QueryCancelAutoPlay</c> window message, but Windows
/// sends that only to the foreground window, and after the administrator prompt the
/// wizard often is not. An <c>IQueryCancelAutoPlay</c> object in the Running Object Table
/// is asked for every AutoPlay event regardless of focus; this one says no to all of
/// them while it is registered. It lives in the (not elevated) launcher, because an
/// elevated registration is invisible to Explorer.
/// </remarks>
internal sealed partial class AutoPlaySuppressor : IDisposable
{
    private static readonly Guid QueryCancelAutoPlayClass = new("331F1768-05A9-4ddd-B86E-DAE34DDC998A");
    private const int RotFlagsRegistrationKeepsAlive = 1;

    private readonly IRunningObjectTable? _table;
    private readonly int _cookie;

    private AutoPlaySuppressor(IRunningObjectTable table, int cookie)
    {
        _table = table;
        _cookie = cookie;
    }

    /// <summary>Starts declining AutoPlay; null if Windows would not take the registration.</summary>
    internal static AutoPlaySuppressor? TryStart()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            if (GetRunningObjectTable(0, out var table) != 0 || table is null) return null;
            var classId = QueryCancelAutoPlayClass;
            if (CreateClassMoniker(ref classId, out var moniker) != 0 || moniker is null) return null;
            var cookie = table.Register(RotFlagsRegistrationKeepsAlive, new Canceller(), moniker);
            return new AutoPlaySuppressor(table, cookie);
        }
        catch (Exception exception) when (exception is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // Without it the window-message answer still applies while the wizard is in front.
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            _table?.Revoke(_cookie);
        }
        catch (COMException)
        {
            // Already gone with the process's COM state; nothing is left registered.
        }
    }

    [ComImport]
    [Guid("DDEFE873-6997-4e68-BE26-39B633ADBE12")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IQueryCancelAutoPlay
    {
        [PreserveSig]
        int AllowAutoPlay(
            [MarshalAs(UnmanagedType.LPWStr)] string path,
            uint contentType,
            [MarshalAs(UnmanagedType.LPWStr)] string label,
            uint serialNumber);
    }

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class Canceller : IQueryCancelAutoPlay
    {
        /// <summary>S_FALSE: AutoPlay is cancelled for this event.</summary>
        public int AllowAutoPlay(string path, uint contentType, string label, uint serialNumber) => 1;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable? table);

    [DllImport("ole32.dll")]
    private static extern int CreateClassMoniker(ref Guid classId, out IMoniker? moniker);
}
#pragma warning restore SYSLIB1054, SYSLIB1096
