using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Studio.Projection.ReadModels;

namespace Aevatar.Studio.Projection.Metadata;

public sealed class ChatHistoryCreateRecoveryCurrentStateDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<ChatHistoryCreateRecoveryCurrentStateDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "studio-chat-history-create-recovery",
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
                ["turn_id"] = Keyword(),
                ["workflow_actor_id"] = Keyword(),
                ["workflow_command_id"] = Keyword(),
                ["workflow_correlation_id"] = Keyword(),
                ["request_fingerprint"] = Keyword(),
                ["status"] = Keyword(),
                ["reserved_at_unix_ms"] = Long(),
                ["completed_at_unix_ms"] = Long(),
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
