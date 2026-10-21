using System.Text.Json;

namespace DexTor.Contracts;

public sealed record SessionStartRequest(
    Guid SessionId,
    string PreferredSystemImagePath,
    string UsbSystemImagePath,
    string DataImagePath,
    string ExpectedSystemImageSha256,
    int MemoryMiB,
    int ProcessorCount,
    // Correlation id for the provisioning run that wrote this device, carried for
    // diagnostics and wire compatibility. It deliberately does not key VM session
    // ownership: it is optional in device.json, so a launcher that could not read it
    // would claim a different lock for the same data.img. Ownership is keyed to the
    // canonical identity of the image itself. Not a credential, never passed to the guest.
    string? DeviceRequestId = null,
    // The wizard's language as a POSIX locale (ru_RU.UTF-8) and the build channel
    // (test or release), handed to the guest through fw_cfg. Null leaves the guest
    // as it boots straight from the stick.
    string? GuestLocale = null,
    string? BuildChannel = null,
    // The guest's screen: the picture area of the VM window maximized on the monitor the
    // launcher is on, in physical pixels, and that monitor's Windows scale in per cent. The
    // guest starts in this mode, so nothing is stretched and no mode change shows. Null
    // leaves QEMU's 1280x800.
    int? DisplayWidth = null,
    int? DisplayHeight = null,
    int? DisplayScalePercent = null);

public static class VmSessionSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string SerializeStartRequest(SessionStartRequest request) =>
        JsonSerializer.Serialize(request, JsonOptions);

    public static SessionStartRequest? DeserializeStartRequest(string json) =>
        JsonSerializer.Deserialize<SessionStartRequest>(json, JsonOptions);
}
