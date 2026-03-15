using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Abstractions.Ports;

public interface IServiceLifecycleQueryPort
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
}
