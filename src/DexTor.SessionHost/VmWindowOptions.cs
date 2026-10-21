namespace DexTor.SessionHost;

/// <summary>
/// The launcher's splash over the place the VM window opens: the DexTor background, the logo
/// and a waiting indicator, covering the monitor's work area.
/// </summary>
/// <remarks>
/// The VM window is shown, moved and maximized under it, and it comes back over the window
/// whenever the guest's screen is QEMU's placeholder or black during the boot. Both calls may
/// come from any thread and return at once.
/// </remarks>
public interface IBootCurtain
{
    void Cover(VmDisplay display);

    void Uncover();
}

/// <summary>How a VM window is revealed: where, behind what, and on which signals.</summary>
/// <param name="Display">The launcher's monitor; null leaves the window where Windows puts it.</param>
/// <param name="Curtain">The splash the window is shown behind; null shows it directly.</param>
/// <param name="Signals">DexTor OS's own signals, once connected.</param>
/// <param name="ShowNow">True when the operator asked to see the window whatever is on it.</param>
/// <param name="Slow">Called when the guest has taken <c>SlowAfter</c> without a picture, and every period after.</param>
public sealed record VmWindowOptions(
    VmDisplay? Display = null,
    IBootCurtain? Curtain = null,
    Func<GuestSignals?>? Signals = null,
    Func<bool>? ShowNow = null,
    Action? Slow = null)
{
    public static VmWindowOptions None { get; } = new();
}
