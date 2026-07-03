using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Audit.Core.Projection;

public sealed class AuditTrailDocumentMetadataProvider : IProjectionDocumentMetadataProvider<Audit.AuditTrailDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        "audit-trail",
        new Dictionary<string, object?>
        {
            ["audit_id"] = "keyword",
            ["content_hash"] = "keyword",
            ["audit_actor_id"] = "keyword",
            ["scope_id"] = "keyword",
            ["operation_name"] = "keyword",
            ["outcome"] = "keyword",
            ["sensitivity_level"] = "keyword",
            ["target_kind"] = "keyword",
            ["target_id"] = "keyword",
            ["request_id"] = "keyword",
            ["command_id"] = "keyword",
            ["correlation_id"] = "keyword",
            ["session_id"] = "keyword",
            ["workflow_run_id"] = "keyword",
            ["occurred_at"] = "date",
            ["updated_at"] = "date",
        },
        new Dictionary<string, object?>(),
        new Dictionary<string, object?>());
}
