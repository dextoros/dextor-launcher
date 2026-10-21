using DexTor.Contracts;
using DexTor.SessionHost;

namespace DexTor.Launcher.Tests;

/// <summary>
/// Injected host adapters, so no test can reach a real volume or a real device.
/// </summary>
/// <remarks>
/// These exist only behind the internal test constructor. The product always constructs
/// <see cref="WindowsVolumeFlusher"/>, <see cref="WindowsRemovableDeviceResolver"/> and
/// <see cref="WindowsDeviceRemover"/>, so a substitute here can never become a shipping
/// success stub.
/// </remarks>
internal sealed class FakeVolumeFlusher(HostFlushResult result, string? detail = null) : IHostVolumeFlusher
{
    /// <summary>Set to block the flush, standing in for a native call that has not returned.</summary>
    public TaskCompletionSource? Gate { get; init; }

    public int Calls { get; private set; }

    public string? LastPath { get; private set; }

    public async Task<(HostFlushResult Result, string? Detail)> FlushVolumeAsync(
        string dataImagePath,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastPath = dataImagePath;
        if (Gate is not null)
        {
            await Gate.Task.ConfigureAwait(false);
        }

        return (result, detail);
    }
}

internal sealed class FakeDeviceResolver(TargetResolution resolution) : IRemovableDeviceResolver
{
    public int Calls { get; private set; }

    public string? LastPath { get; private set; }

    public string? LastExpectedVolume { get; private set; }

    public TargetResolution Resolve(string dataImagePath, string? expectedVolumeGuidPath = null)
    {
        Calls++;
        LastPath = dataImagePath;
        LastExpectedVolume = expectedVolumeGuidPath;
        return resolution;
    }

    public static FakeDeviceResolver Resolving(string volume = @"\\?\Volume{11111111-2222-3333-4444-555555555555}\") =>
        new(new TargetResolution(new RemovableTarget(volume, @"USBSTOR\DISK&VEN_DEXTOR"), null));

    public static FakeDeviceResolver Refusing(string reason) => new(new TargetResolution(null, reason));
}

internal sealed class FakeDeviceRemover(DeviceRemovalResult result, string? detail = null) : IDeviceRemover
{
    public int Calls { get; private set; }

    public RemovableTarget? LastTarget { get; private set; }

    /// <summary>Observed at the moment removal was requested, to prove ordering.</summary>
    public Func<bool>? ObserveOnCall { get; init; }

    public bool ObservationAtCall { get; private set; }

    public Task<(DeviceRemovalResult Result, string? Detail)> RequestRemovalAsync(
        RemovableTarget target,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastTarget = target;
        if (ObserveOnCall is not null)
        {
            ObservationAtCall = ObserveOnCall();
        }

        return Task.FromResult((result, detail));
    }
}
