using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceServingQueryPort
{
    Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceRolloutCommandObservationSnapshot?> GetServiceRolloutCommandObservationAsync(
        ServiceIdentity identity,
        string commandId,
        CancellationToken ct = default);

    Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
