using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit.Core.Projection;

public sealed partial class AuditTrailArtifactStorageDocument
    : IProjectionReadModel<AuditTrailArtifactStorageDocument>
{
    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue?.ToDateTimeOffset() ?? default;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
