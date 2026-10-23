using System.Threading;
using System.Threading.Tasks;
using DexTor.Contracts;

namespace DexTor.Updater;

public interface IUpdateEngine
{
    Task<UpdatePlan> CheckAsync(string channel, CancellationToken cancellationToken = default);
}

public sealed class UpdateEngine : IUpdateEngine
{
    public Task<UpdatePlan> CheckAsync(string channel, CancellationToken cancellationToken = default)
    {
        // The Store updates its own package; a launcher that replaced itself would break
        // the package's signature and fight the Store. DexTor OS images are not affected:
        // they are data, downloaded by the launcher as before.
        if (PackagedApp.IsPackaged)
        {
            return Task.FromResult(new UpdatePlan("0.0.0", "0.0.0", UpdatePolicy.Deferred));
        }

        var plan = new UpdatePlan("0.1.0", "0.0.0", string.IsNullOrWhiteSpace(channel) ? UpdatePolicy.Required : UpdatePolicy.AutoInstallOnDemand);
        return Task.FromResult(plan);
    }
}
