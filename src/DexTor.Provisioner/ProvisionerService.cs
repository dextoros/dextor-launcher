using System.Threading;
using System.Threading.Tasks;

namespace DexTor.Provisioner;

public interface IProvisionerService
{
    Task<ProvisionerResult> ExecuteAsync(ProvisionerRequest request, CancellationToken cancellationToken = default);
}

public sealed class ProvisionerService : IProvisionerService
{
    public Task<ProvisionerResult> ExecuteAsync(ProvisionerRequest request, CancellationToken cancellationToken = default)
    {
        var result = request.Operation is ProvisionerOperation.EnableWhpx or
            ProvisionerOperation.InspectUsbDevice or
            ProvisionerOperation.LockUsbVolumes or
            ProvisionerOperation.DismountUsbVolumes or
            ProvisionerOperation.InitializeUsb or
            ProvisionerOperation.CreateUsbVolume or
            ProvisionerOperation.FormatUsbVolume or
            ProvisionerOperation.CreateHybridUsb or
            ProvisionerOperation.FlushUsbDevice or
            ProvisionerOperation.EjectUsbDevice
            ? new ProvisionerResult(request.Operation, true)
            : throw new InvalidOperationException("Invalid provisioner operation");

        return Task.FromResult(result);
    }
}
