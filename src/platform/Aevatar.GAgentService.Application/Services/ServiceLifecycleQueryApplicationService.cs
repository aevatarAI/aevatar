using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;

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

    // Refactor (iter34/cluster-006-artifact-projectors-state-root):
    // Old pattern: ServiceCatalogReadModel carried active deployment fields mutated by the catalog projector.
    // New principle: catalog queries compose serving facts from deployment readmodels, keeping each readmodel actor-scoped.
    public async Task<ServiceCatalogSnapshot?> GetServiceAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var service = await _catalogQueryReader.GetAsync(identity, ct);
        return service == null ? null : await ComposeActiveDeploymentAsync(identity, service, ct);
    }

    // Refactor (iter34/cluster-006-artifact-projectors-state-root):
    // Old pattern: list queries returned deployment fields previously stored on each catalog readmodel.
    // New principle: list queries enrich each definition snapshot from the deployment readmodel for that service.
    public async Task<IReadOnlyList<ServiceCatalogSnapshot>> ListServicesAsync(
        string tenantId,
        string appId,
        string @namespace,
        int take = 200,
        CancellationToken ct = default)
    {
        var services = await _catalogQueryReader.QueryByScopeAsync(tenantId, appId, @namespace, take, ct);
        var enriched = new List<ServiceCatalogSnapshot>(services.Count);
        foreach (var service in services)
        {
            enriched.Add(await ComposeActiveDeploymentAsync(
                BuildIdentity(service),
                service,
                ct));
        }

        return enriched;
    }

    public Task<ServiceRevisionCatalogSnapshot?> GetServiceRevisionsAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _revisionCatalogQueryReader.GetAsync(identity, ct);

    public Task<ServiceDeploymentCatalogSnapshot?> GetServiceDeploymentsAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _deploymentQueryReader.GetAsync(identity, ct);

    // Refactor (iter34/cluster-006-artifact-projectors-state-root):
    // Old pattern: active deployment was a denormalized mutation owned by the service catalog projector.
    // New principle: active deployment is selected from deployment readmodels during query composition.
    private async Task<ServiceCatalogSnapshot> ComposeActiveDeploymentAsync(
        ServiceIdentity identity,
        ServiceCatalogSnapshot service,
        CancellationToken ct)
    {
        var deploymentCatalog = await _deploymentQueryReader.GetAsync(identity, ct);
        var activeDeployment = deploymentCatalog?.Deployments
            .Where(static x =>
                string.Equals(x.Status, ServiceDeploymentStatus.Active.ToString(), StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(x.PrimaryActorId))
            .OrderByDescending(static x => x.UpdatedAt)
            .ThenBy(static x => x.DeploymentId, StringComparer.Ordinal)
            .FirstOrDefault();

        return activeDeployment == null
            ? service
            : service with
            {
                ActiveServingRevisionId = activeDeployment.RevisionId,
                DeploymentId = activeDeployment.DeploymentId,
                PrimaryActorId = activeDeployment.PrimaryActorId,
                DeploymentStatus = activeDeployment.Status,
            };
    }

    private static ServiceIdentity BuildIdentity(ServiceCatalogSnapshot service) =>
        new()
        {
            TenantId = service.TenantId,
            AppId = service.AppId,
            Namespace = service.Namespace,
            ServiceId = service.ServiceId,
        };
}
