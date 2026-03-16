using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Metadata;

public sealed class ServiceDeploymentCatalogReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<ServiceDeploymentCatalogReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-deployments",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
