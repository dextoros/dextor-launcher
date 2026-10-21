using System.Threading;
using System.Threading.Tasks;

namespace DexTor.Updater;

public interface IUpdateEngine
{
    Task<UpdatePlan> CheckAsync(string channel, CancellationToken cancellationToken = default);
}

public sealed class UpdateEngine : IUpdateEngine
{
    public Task<UpdatePlan> CheckAsync(string channel, CancellationToken cancellationToken = default)
    {
        var plan = new UpdatePlan("0.1.0", "0.0.0", string.IsNullOrWhiteSpace(channel) ? UpdatePolicy.Required : UpdatePolicy.AutoInstallOnDemand);
        return Task.FromResult(plan);
    }
}
