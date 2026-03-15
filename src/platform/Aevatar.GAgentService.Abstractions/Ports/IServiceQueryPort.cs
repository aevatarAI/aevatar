using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceQueryPort
{
    Task<ServiceCatalogSnapshot?> GetServiceAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
        string tenantId,
        string appId,
        string @namespace,
        int take = 200,
        CancellationToken ct = default);

    Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);

    Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(
        ServiceIdentity identity,
        CancellationToken ct = default);
}
