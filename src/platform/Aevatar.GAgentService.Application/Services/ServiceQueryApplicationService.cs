using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceQueryApplicationService : IServiceQueryPort
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;
    private readonly IServiceDeploymentCatalogQueryReader _deploymentQueryReader;
    private readonly IServiceServingSetQueryReader _servingSetQueryReader;
    private readonly IServiceRolloutQueryReader _rolloutQueryReader;
    private readonly IServiceTrafficViewQueryReader _trafficViewQueryReader;

    public ServiceQueryApplicationService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader,
        IServiceDeploymentCatalogQueryReader deploymentQueryReader,
        IServiceServingSetQueryReader servingSetQueryReader,
        IServiceRolloutQueryReader rolloutQueryReader,
        IServiceTrafficViewQueryReader trafficViewQueryReader)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
        _deploymentQueryReader = deploymentQueryReader ?? throw new ArgumentNullException(nameof(deploymentQueryReader));
        _servingSetQueryReader = servingSetQueryReader ?? throw new ArgumentNullException(nameof(servingSetQueryReader));
        _rolloutQueryReader = rolloutQueryReader ?? throw new ArgumentNullException(nameof(rolloutQueryReader));
        _trafficViewQueryReader = trafficViewQueryReader ?? throw new ArgumentNullException(nameof(trafficViewQueryReader));
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

    public Task<ServiceServingSetSnapshot?> GetServiceServingSetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _servingSetQueryReader.GetAsync(identity, ct);

    public Task<ServiceRolloutSnapshot?> GetServiceRolloutAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _rolloutQueryReader.GetAsync(identity, ct);

    public Task<ServiceTrafficViewSnapshot?> GetServiceTrafficViewAsync(
        ServiceIdentity identity,
        CancellationToken ct = default) =>
        _trafficViewQueryReader.GetAsync(identity, ct);
}
