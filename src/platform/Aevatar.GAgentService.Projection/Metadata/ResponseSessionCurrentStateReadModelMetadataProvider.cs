using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GAgentService.Projection.ReadModels;

namespace Aevatar.GAgentService.Projection.Metadata;

public sealed class ResponseSessionCurrentStateReadModelMetadataProvider
    : IProjectionDocumentMetadataProvider<ResponseSessionCurrentStateReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "gagent-service-response-sessions",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
