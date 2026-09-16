using Aevatar.CQRS.Projection.Stores.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

// Refactor (iter17/cluster-034):
//   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
//   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
public sealed partial class ProjectionScopeStatusDocument
    : IProjectionReadModel<ProjectionScopeStatusDocument>, IProjectionRouteFencedReadModel
{
    string IProjectionReadModel.ActorId => ScopeActorId;

    // The source scope's committed route epoch at the time of the write; 0 for documents
    // written by binaries that did not carry the route. Same-version takeover is allowed only
    // under a strictly higher epoch (see IProjectionRouteFencedReadModel).
    long IProjectionRouteFencedReadModel.RouteEpoch => StatusRoute?.RouteEpoch ?? 0;

    public DateTimeOffset UpdatedAt
    {
        get => UpdatedAtUtcValue != null ? UpdatedAtUtcValue.ToDateTimeOffset() : default;
        set => UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(value.ToUniversalTime());
    }
}
