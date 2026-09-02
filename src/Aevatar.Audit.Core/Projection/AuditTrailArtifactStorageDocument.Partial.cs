using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Core.Projection;

public sealed partial class AuditTrailArtifactStorageDocument
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue?.ToDateTimeOffset() ?? default;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public static AuditTrailArtifactStorageDocument FromArtifact(Audit.AuditTrailDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new AuditTrailArtifactStorageDocument
        {
            Id = document.AuditId,
            UpdatedAtUtcValue = document.UpdatedAt?.Clone(),
            Artifact = document.Clone(),
        };
    }
}
