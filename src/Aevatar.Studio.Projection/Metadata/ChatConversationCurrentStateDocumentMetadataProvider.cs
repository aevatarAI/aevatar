using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class ChatConversationCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ChatConversationCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-chat-conversation",
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
                ["scope_id"] = Keyword(),
                ["conversation_id"] = Keyword(),
                ["title"] = Keyword(),
                ["service_id"] = Keyword(),
                ["service_kind"] = Keyword(),
                ["created_at_ms"] = Long(),
                ["updated_at_ms"] = Long(),
                ["message_count"] = Integer(),
                ["llm_route"] = Keyword(),
                ["llm_model"] = Keyword(),
                ["deleted"] = Boolean(),
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

    private static Dictionary<string, object?> Integer() => new(StringComparer.Ordinal)
    {
        ["type"] = "integer",
    };

    private static Dictionary<string, object?> Date() => new(StringComparer.Ordinal)
    {
        ["type"] = "date",
    };

    private static Dictionary<string, object?> Boolean() => new(StringComparer.Ordinal)
    {
        ["type"] = "boolean",
    };
}
