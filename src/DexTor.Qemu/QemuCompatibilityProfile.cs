namespace DexTor.Qemu;

/// <summary>
/// One way of starting the virtual machine, from the fastest a computer may take to the
/// one every computer takes.
/// </summary>
/// <param name="Name">Stable name, remembered between starts.</param>
/// <param name="CpuModel">The <c>-cpu</c> value; null keeps QEMU's default (qemu64).</param>
public sealed record QemuCompatibilityProfile(string Name, string? CpuModel)
{
    /// <summary>
    /// QEMU's default CPU: no AES-NI, no AVX, so Tor, the wallet and the browser run their
    /// slow software paths - but WHPX takes it everywhere.
    /// </summary>
    public static readonly QemuCompatibilityProfile Compatible = new("compatible", null);
}
