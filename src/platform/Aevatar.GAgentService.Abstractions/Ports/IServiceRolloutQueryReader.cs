using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceRolloutQueryReader
{
    Task<ServiceRolloutSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
