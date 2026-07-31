using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Audit.Core.Projection;

public sealed class AuditTrailDocumentMetadataProvider
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "audit-trail",
        Mappings: BuildMappings(),
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));

    private static IReadOnlyDictionary<string, object?> BuildMappings()
    {
        var chat = Object(
            ("surface", Field("keyword")),
            ("conversation_id", Field("keyword")),
            ("turn_id", Field("keyword")),
            ("task_id", Field("keyword")),
            ("step_id", Field("keyword")),
            ("action_request_id", Field("keyword")));
        var provenance = Object(("chat", chat));
        var correlation = Object(
            ("causation_id", TextWithKeyword()),
            ("call_id", TextWithKeyword()),
            ("approval_id", TextWithKeyword()));
        var record = Object(
            ("identity_key_id", TextWithKeyword()),
            ("actor_kind", TextWithKeyword()),
            ("operation_kind", TextWithKeyword()),
            ("capture_plane", TextWithKeyword()),
            ("correlation", correlation),
            ("provenance", provenance));
        var artifact = Object(
            ("occurred_at", Field("date")),
            ("recorded_at", Field("date")),
            ("schema_version", Field("keyword")),
            ("scope_id", TextWithKeyword()),
            ("audit_actor_id", TextWithKeyword()),
            ("operation_name", TextWithKeyword()),
            ("target_kind", TextWithKeyword()),
            ("target_id", TextWithKeyword()),
            ("trace_id", TextWithKeyword()),
            ("correlation_id", TextWithKeyword()),
            ("request_id", TextWithKeyword()),
            ("command_id", TextWithKeyword()),
            ("session_id", TextWithKeyword()),
            ("workflow_run_id", TextWithKeyword()),
            ("committed_event_id", TextWithKeyword()),
            ("committed_actor_id", TextWithKeyword()),
            ("committed_actor_type", TextWithKeyword()),
            ("committed_event_type_url", TextWithKeyword()),
            ("committed_state_version", Field("long")),
            ("outcome", TextWithKeyword()),
            ("lifecycle_phase", TextWithKeyword()),
            ("terminal_outcome", TextWithKeyword()),
            ("sensitivity_level", TextWithKeyword()),
            ("record", record));

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
            ["properties"] = Properties(
                ("id", TextWithKeyword()),
                ("updated_at_utc_value", Field("date")),
                ("artifact", artifact)),
        };
    }

    private static Dictionary<string, object?> Properties(
        params (string Name, object? Mapping)[] fields) =>
        fields.ToDictionary(
            static field => field.Name,
            static field => field.Mapping,
            StringComparer.Ordinal);

    private static Dictionary<string, object?> Object(
        params (string Name, object? Mapping)[] fields) =>
        new(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["dynamic"] = true,
            ["properties"] = Properties(fields),
        };

    private static Dictionary<string, object?> Field(string type) =>
        new(StringComparer.Ordinal)
        {
            ["type"] = type,
        };

    private static Dictionary<string, object?> TextWithKeyword() =>
        new(StringComparer.Ordinal)
        {
            ["type"] = "text",
            ["fields"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["keyword"] = Field("keyword"),
            },
        };
}
