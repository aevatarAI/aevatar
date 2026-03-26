using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Metadata;

public sealed class GroupTimelineReadModelMetadataProvider : IProjectionDocumentMetadataProvider<GroupTimelineReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "group-chat-timeline",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
