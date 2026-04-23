using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceRolloutCommandObservationQueryReader
{
    Task<ServiceRolloutCommandObservationSnapshot?> GetAsync(
        string commandId,
        CancellationToken ct = default);
}
