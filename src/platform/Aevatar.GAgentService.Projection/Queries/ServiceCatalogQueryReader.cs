using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceCatalogQueryReader : IServiceCatalogQueryReader
{
    private readonly IProjectionDocumentStore<ServiceCatalogReadModel, string> _documentStore;

    public ServiceCatalogQueryReader(IProjectionDocumentStore<ServiceCatalogReadModel, string> documentStore)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
    }

    public async Task<ServiceCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        return readModel == null ? null : Map(readModel);
    }

    public async Task<IReadOnlyList<ServiceCatalogSnapshot>> ListAsync(
        string tenantId,
        string appId,
        string @namespace,
        int take = 200,
        CancellationToken ct = default)
    {
        var boundedTake = Math.Clamp(take, 1, 1000);
        var serviceKeyPrefix = $"{tenantId}:{appId}:{@namespace}:";
        var items = await _documentStore.ListAsync(boundedTake * 5, ct);
        return items
            .Where(x => x.Id.StartsWith(serviceKeyPrefix, StringComparison.Ordinal))
            .Take(boundedTake)
            .Select(Map)
            .ToList();
    }

    private static ServiceCatalogSnapshot Map(ServiceCatalogReadModel readModel)
    {
        return new ServiceCatalogSnapshot(
            readModel.Id,
            readModel.TenantId,
            readModel.AppId,
            readModel.Namespace,
            readModel.ServiceId,
            readModel.DisplayName,
            readModel.DefaultServingRevisionId,
            readModel.ActiveServingRevisionId,
            readModel.DeploymentId,
            readModel.PrimaryActorId,
            readModel.DeploymentStatus,
            readModel.Endpoints
                .Select(x => new ServiceEndpointSnapshot(
                    x.EndpointId,
                    x.DisplayName,
                    x.Kind,
                    x.RequestTypeUrl,
                    x.ResponseTypeUrl,
                    x.Description))
                .ToList(),
            readModel.UpdatedAt);
    }
}
