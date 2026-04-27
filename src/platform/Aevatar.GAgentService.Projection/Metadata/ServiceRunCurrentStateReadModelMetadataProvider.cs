using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Metadata;

public sealed class ServiceRunCurrentStateReadModelMetadataProvider : IProjectionDocumentMetadataProvider<ServiceRunCurrentStateReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-runs",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
