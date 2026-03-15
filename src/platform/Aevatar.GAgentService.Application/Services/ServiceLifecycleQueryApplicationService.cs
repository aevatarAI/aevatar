using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceLifecycleQueryApplicationService : IServiceLifecycleQueryPort
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;
    private readonly IServiceDeploymentCatalogQueryReader _deploymentQueryReader;

    public ServiceLifecycleQueryApplicationService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        IServiceDeploymentCatalogQueryReader deploymentQueryReader)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
        _deploymentQueryReader = deploymentQueryReader ?? throw new ArgumentNullException(nameof(deploymentQueryReader));
    }

    public Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default) =>
        _catalogQueryReader.GetAsync(identity, ct);

    public Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
        string tenantId,
        string appId,
        string @namespace,
        int take = 200,
        CancellationToken ct = default) =>
        _catalogQueryReader.ListAsync(tenantId, appId, @namespace, take, ct);

    public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _revisionCatalogQueryReader.GetAsync(identity, ct);

    public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _deploymentQueryReader.GetAsync(identity, ct);
}
