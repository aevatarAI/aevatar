using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
using Google.Protobuf.WellKnownTypes;

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

        var scopeKey = new ProjectionRuntimeScopeKey(
            state.RootActorId,
            state.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(state.Mode),
            state.SessionId);
        var scopeActorId = ProjectionScopeActorId.Build(scopeKey);
        var updatedAt = CommittedStateEventEnvelope.ResolveTimestamp(envelope, _clock.UtcNow);

        var document = new ProjectionScopeStatusDocument
        {
            Id = scopeActorId,
            ScopeActorId = scopeActorId,
            StateVersion = stateEvent.Version,
            LastEventId = stateEvent.EventId ?? string.Empty,
            UpdatedAtUtcValue = Timestamp.FromDateTimeOffset(updatedAt.ToUniversalTime()),
            RootActorId = state.RootActorId ?? string.Empty,
            ProjectionKind = state.ProjectionKind ?? string.Empty,
            SessionId = state.SessionId ?? string.Empty,
            Mode = state.Mode,
            Active = state.Active,
            ObservationAttached = state.ObservationAttached,
            Released = state.Released,
            HighestSeenVersion = state.HighestSeenVersion,
            LastSuccessfulVersion = state.LastSuccessfulVersion,
            UnresolvedFailureCount = state.Failures.Count,
            ReceivedEnvelopeTotal = state.ReceivedEnvelopeTotal,
            AttemptedEnvelopeTotal = state.AttemptedEnvelopeTotal,
            SuccessfulMaterializationTotal = state.SuccessfulMaterializationTotal,
            FailedAttemptTotal = state.FailedAttemptTotal,
            RetryExhaustedTotal = state.RetryExhaustedTotal,
            RetryExhaustedFailureCount = state.Failures.Count(failure => failure.RetryExhausted),
            FailureDiagnosticDroppedTotal = state.FailureDiagnosticDroppedTotal,
        };
        var oldestFailure = state.Failures
            .Where(failure => failure.OccurredAtUtc != null)
            .OrderBy(failure => failure.OccurredAtUtc)
            .FirstOrDefault();
        document.OldestUnresolvedFailureAtUtc = oldestFailure?.OccurredAtUtc?.Clone();
        document.RecentObservedEnvelopes.Add(state.RecentObservedEnvelopes);
        foreach (var sourceActorId in state.HighestSeenVersionsByActor.Keys
                     .Union(state.LastSuccessfulVersionsByActor.Keys, StringComparer.Ordinal)
                     .OrderBy(sourceActorId => sourceActorId, StringComparer.Ordinal))
        {
            var highestSeen = state.HighestSeenVersionsByActor.TryGetValue(sourceActorId, out var seen)
                ? seen
                : 0;
            var lastSuccessful = state.LastSuccessfulVersionsByActor.TryGetValue(sourceActorId, out var successful)
                ? successful
                : 0;
            document.SourceVersions.Add(new ProjectionSourceVersionStatus
            {
                SourceActorId = sourceActorId,
                HighestSeenVersion = highestSeen,
                LastSuccessfulVersion = lastSuccessful,
                VersionGap = Math.Max(0, highestSeen - lastSuccessful),
            });
        }

        await _writeDispatcher.UpsertAsync(document, ct);
    }
}
