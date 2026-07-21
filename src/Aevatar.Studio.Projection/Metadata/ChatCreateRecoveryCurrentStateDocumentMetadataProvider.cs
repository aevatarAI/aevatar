using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class ChatCreateRecoveryCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ChatCreateRecoveryCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-chat-create-recovery",
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
                ["create_idempotency_key"] = Keyword(),
                ["create_request_hash"] = Keyword(),
                ["conversation_id"] = Keyword(),
                ["turn_id"] = Keyword(),
                ["status"] = Keyword(),
                ["source_version"] = Long(),
                ["delivery_actor_id"] = Keyword(),
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
