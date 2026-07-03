using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Audit;

public sealed partial class AuditTrailDocument : IProjectionReadModel<AuditTrailDocument>
{
    string IProjectionReadModel.ActorId => AuditActorId;

    long IProjectionReadModel.StateVersion => CommittedStateVersion > 0 ? CommittedStateVersion : 1;

    string IProjectionReadModel.LastEventId => ContentHash;

    DateTimeOffset IProjectionReadModel.UpdatedAt => UpdatedAt?.ToDateTimeOffset() ?? default;

    public DateTimeOffset OccurredAtDateTimeOffset
    {
        get => OccurredAt?.ToDateTimeOffset() ?? default;
        set => OccurredAt = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }

    public DateTimeOffset UpdatedAtDateTimeOffset
    {
        get => UpdatedAt?.ToDateTimeOffset() ?? default;
        set => UpdatedAt = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
