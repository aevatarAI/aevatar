using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class NyxIdChatConversationCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<NyxIdChatConversationCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-nyxid-chat-conversation",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
            ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Keyword(),
                ["actor_id"] = Keyword(),
                ["state_version"] = Long(),
                ["last_event_id"] = Keyword(),
                ["updated_at"] = Date(),
                ["conversation_actor_id"] = Keyword(),
                ["scope_id"] = Keyword(),
                ["progress_sequence"] = Long(),
            },
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static Dictionary<string, object?> Keyword() => new(StringComparer.Ordinal)
    {
        ["type"] = "keyword",
    };

    private static Dictionary<string, object?> Long() => new(StringComparer.Ordinal)
    {
        ["type"] = "long",
    };

    private static Dictionary<string, object?> Date() => new(StringComparer.Ordinal)
    {
        ["type"] = "date",
    };
}
