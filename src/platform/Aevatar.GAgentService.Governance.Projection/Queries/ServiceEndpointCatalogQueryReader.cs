using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Queries;

public sealed class ServiceEndpointCatalogQueryReader : IServiceEndpointCatalogQueryReader
{
    private readonly IProjectionDocumentStore<ServiceEndpointCatalogReadModel, string> _documentStore;

    public ServiceEndpointCatalogQueryReader(IProjectionDocumentStore<ServiceEndpointCatalogReadModel, string> documentStore)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
    }

    public async Task<ServiceEndpointCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceEndpointCatalogSnapshot(
            readModel.Id,
            readModel.Endpoints
                .OrderBy(x => x.EndpointId, StringComparer.Ordinal)
                .Select(x => new ServiceEndpointExposureSnapshot(
                    x.EndpointId,
                    x.DisplayName,
                    x.Kind,
                    x.RequestTypeUrl,
                    x.ResponseTypeUrl,
                    x.Description,
                    x.ExposureKind,
                    [.. x.PolicyIds]))
                .ToList(),
            readModel.UpdatedAt);
    }
}
