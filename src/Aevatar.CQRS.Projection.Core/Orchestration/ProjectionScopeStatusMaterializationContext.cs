namespace Aevatar.CQRS.Projection.Core.Orchestration;

// Refactor (iter17/cluster-034):
//   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
//   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
//   refactor helper, no behavior change.
public sealed class ProjectionScopeStatusMaterializationContext
    : IProjectionMaterializationContext
{
    public const string ProjectionKindValue = "projection-scope-status";

    public required string RootActorId { get; init; }

    public string ProjectionKind => ProjectionKindValue;
}
