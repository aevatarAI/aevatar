using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

// Refactor (iter17/cluster-034):
//   Old pattern: Replay-based projection scope watermark query via IEventStore (EventStoreProjectionScopeWatermarkQueryPort).
//   New principle: Materialized ProjectionScopeStatusDocument readmodel; ProjectionScopeStatusQueryPort reads document only; never replays IEventStore.
[ProjectionExempt(
    Category = ProjectionExemptionCategory.ProjectionCoreStatus,
    Reason = "Projection runtime status is activated internally when projection scopes start; it is not a feature readmodel with a committed-state plan provider.")]
public sealed class ProjectionScopeStatusProjector
    : ICurrentStateProjectionMaterializer<ProjectionScopeStatusMaterializationContext>
{
    private readonly IProjectionWriteDispatcher<ProjectionScopeStatusDocument> _writeDispatcher;
    private readonly IProjectionClock _clock;

    public ProjectionScopeStatusProjector(
        IProjectionWriteDispatcher<ProjectionScopeStatusDocument> writeDispatcher,
        IProjectionClock clock)
    {
        _writeDispatcher = writeDispatcher ?? throw new ArgumentNullException(nameof(writeDispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async ValueTask ProjectAsync(
        ProjectionScopeStatusMaterializationContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);

        if (!CommittedStateEventEnvelope.TryUnpackState<ProjectionScopeState>(
                envelope,
                out _,
                out var stateEvent,
                out var state) ||
            stateEvent == null ||
            state == null)
        {
            return;
        }

        // The source scope actor owns the status write route. Once it has committed a terminal
        // route this legacy shadow scope is no longer an authoritative writer: it may still be
        // draining already-delivered envelopes before it is released, and must not write.
        if (ProjectionScopeStatusRoutePolicy.IsTerminalRoute(state.StatusRoute))
            return;

        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);
        await _writeDispatcher.UpsertAsync(
            ProjectionScopeStatusDocumentMapper.Map(state, stateEvent, updatedAt),
            ct);
    }
}
