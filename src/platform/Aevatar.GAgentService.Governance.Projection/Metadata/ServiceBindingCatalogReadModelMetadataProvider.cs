using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Governance.Projection.ReadModels;

namespace Aevatar.GAgentService.Governance.Projection.Metadata;

public sealed class ServiceBindingCatalogReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<ServiceBindingCatalogReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-bindings",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
