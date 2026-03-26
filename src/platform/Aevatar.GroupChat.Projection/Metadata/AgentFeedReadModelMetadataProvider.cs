using Aevatar.GroupChat.Projection.ReadModels;

namespace Aevatar.GroupChat.Projection.Metadata;

public sealed class AgentFeedReadModelMetadataProvider : IProjectionDocumentMetadataProvider<AgentFeedReadModel>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "group-chat-agent-feed",
        Mappings: new Dictionary<string, object?>(),
        Settings: new Dictionary<string, object?>(),
        Aliases: new Dictionary<string, object?>());
}
