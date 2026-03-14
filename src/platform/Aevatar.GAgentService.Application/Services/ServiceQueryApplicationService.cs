using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;

namespace Aevatar.GAgentService.Application.Services;

public sealed class ServiceQueryApplicationService : IServiceQueryPort
{
    private readonly IServiceCatalogQueryReader _catalogQueryReader;
    private readonly IServiceRevisionCatalogQueryReader _revisionCatalogQueryReader;

    public ServiceQueryApplicationService(
        IServiceCatalogQueryReader catalogQueryReader,
        IServiceRevisionCatalogQueryReader revisionCatalogQueryReader)
    {
        _catalogQueryReader = catalogQueryReader ?? throw new ArgumentNullException(nameof(catalogQueryReader));
        _revisionCatalogQueryReader = revisionCatalogQueryReader ?? throw new ArgumentNullException(nameof(revisionCatalogQueryReader));
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
}
