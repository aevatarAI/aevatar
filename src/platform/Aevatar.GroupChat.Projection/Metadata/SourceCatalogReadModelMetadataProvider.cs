using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Metadata;

public sealed class SourceCatalogReadModelMetadataProvider : IProjectionDocumentMetadataProvider<SourceCatalogReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "group-chat-source-catalog",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
