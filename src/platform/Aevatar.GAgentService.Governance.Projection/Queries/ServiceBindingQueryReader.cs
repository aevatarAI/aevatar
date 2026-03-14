using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Governance.Abstractions.Ports;
using Aevatar.GAgentService.Governance.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Queries;

public sealed class ServiceBindingQueryReader : IServiceBindingQueryReader
{
    private readonly IProjectionDocumentStore<ServiceBindingCatalogReadModel, string> _documentStore;

    public ServiceBindingQueryReader(IProjectionDocumentStore<ServiceBindingCatalogReadModel, string> documentStore)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
    }

    public async Task<ServiceBindingCatalogSnapshot?> GetAsync(ServiceIdentity identity, CancellationToken ct = default)
    {
        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceBindingCatalogSnapshot(
            readModel.Id,
            readModel.Bindings
                .OrderBy(x => x.BindingId, StringComparer.Ordinal)
                .Select(x => new ServiceBindingSnapshot(
                    x.BindingId,
                    x.DisplayName,
                    x.BindingKind,
                    [.. x.PolicyIds],
                    x.Retired,
                    string.IsNullOrWhiteSpace(x.TargetServiceKey) ? null : x.TargetServiceKey,
                    string.IsNullOrWhiteSpace(x.TargetEndpointId) ? null : x.TargetEndpointId,
                    string.IsNullOrWhiteSpace(x.ConnectorType) ? null : x.ConnectorType,
                    string.IsNullOrWhiteSpace(x.ConnectorId) ? null : x.ConnectorId,
                    string.IsNullOrWhiteSpace(x.SecretName) ? null : x.SecretName))
                .ToList(),
            readModel.UpdatedAt);
    }
}
