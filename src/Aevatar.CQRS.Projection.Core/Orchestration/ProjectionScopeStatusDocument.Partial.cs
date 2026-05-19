using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

// Refactor (iter17/cluster-034):
//   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
//   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
public sealed partial class ProjectionScopeStatusDocument
    : IProjectionReadModel<ProjectionScopeStatusDocument>
{
    string IProjectionReadModel.ActorId => ScopeActorId;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue != null ? UpdatedAtUtcValue.ToDateTimeOffset() : default;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
