using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Metadata;

public sealed class ServiceEndpointCatalogReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<ServiceEndpointCatalogReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-endpoint-catalog",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
