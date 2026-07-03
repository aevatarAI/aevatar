using Aevatar.CQRS.Projection.Stores.Abstractions;

namespace Aevatar.Audit.Core.Projection;

public sealed class AuditTrailDocumentMetadataProvider
    : IProjectionDocumentMetadataProvider<AuditTrailArtifactStorageDocument>
{
    public DocumentIndexMetadata Metadata { get; } = new(
        IndexName: "audit-trail",
        Mappings: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["dynamic"] = true,
        },
        Settings: new Dictionary<string, object?>(StringComparer.Ordinal),
        Aliases: new Dictionary<string, object?>(StringComparer.Ordinal));
}
