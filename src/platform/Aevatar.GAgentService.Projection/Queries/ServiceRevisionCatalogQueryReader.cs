using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.GAgentService.Abstractions.Queries;
using Aevatar.GAgentService.Abstractions.Services;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Queries;

public sealed class ServiceRevisionCatalogQueryReader : IServiceRevisionCatalogQueryReader
{
    private readonly IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> _documentStore;

    public ServiceRevisionCatalogQueryReader(
        IProjectionDocumentReader<ServiceRevisionCatalogReadModel, string> documentStore)
    {
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
    }

    public async Task<ServiceRevisionCatalogSnapshot?> GetAsync(
        ServiceIdentity identity,
        CancellationToken ct = default)
    {
        var readModel = await _documentStore.GetAsync(ServiceKeys.Build(identity), ct);
        if (readModel == null)
            return null;

        return new ServiceRevisionCatalogSnapshot(
            readModel.Id,
            readModel.Revisions
                .OrderByDescending(x => x.CreatedAt ?? DateTimeOffset.MinValue)
                .Select(x => new ServiceRevisionSnapshot(
                    x.RevisionId,
                    x.ImplementationKind,
                    x.Status,
                    x.ArtifactHash,
                    x.FailureReason,
                    x.Endpoints
                        .Select(y => new ServiceEndpointSnapshot(
                            y.EndpointId,
                            y.DisplayName,
                            y.Kind,
                            y.RequestTypeUrl,
                            y.ResponseTypeUrl,
                            y.Description))
                        .ToList(),
                    x.CreatedAt,
                    x.PreparedAt,
                    x.PublishedAt,
                    x.RetiredAt))
                .ToList(),
            readModel.UpdatedAt);
    }
}
