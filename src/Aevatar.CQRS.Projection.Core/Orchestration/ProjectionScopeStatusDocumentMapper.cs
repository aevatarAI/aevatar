using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Maps one committed <see cref="ProjectionScopeState"/> at one authoritative state-event
/// version to the status read model. Both status writers (the legacy shadow scope and the
/// terminal materializer) share this mapping so a same-version write from either under the
/// same route epoch is byte-identical (exact duplicate); the mapped route epoch is the
/// document's write fence, so a writer under a higher route epoch takes the same version over.
/// </summary>
internal static class ProjectionScopeStatusDocumentMapper
{
    public static ProjectionScopeStatusDocument Map(
        ProjectionScopeState state,
        StateEvent stateEvent,
        DateTimeOffset updatedAt)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(stateEvent);

        var scopeActorId = ProjectionScopeActorId.Build(new ProjectionRuntimeScopeKey(
            state.RootActorId,
            state.ProjectionKind,
            ProjectionScopeModeMapper.ToRuntime(state.Mode),
            state.SessionId));
        var failureSummary = state.FailureSummary ?? ProjectionScopeFailureLog.BuildSummary(state.Failures);

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
            UnresolvedFailureCount = failureSummary.UnresolvedFailureCount,
            ReceivedEnvelopeTotal = state.ReceivedEnvelopeTotal,
            AttemptedEnvelopeTotal = state.AttemptedEnvelopeTotal,
            SuccessfulMaterializationTotal = state.SuccessfulMaterializationTotal,
            FailedAttemptTotal = state.FailedAttemptTotal,
            RetryExhaustedTotal = state.RetryExhaustedTotal,
            RetryExhaustedFailureCount = failureSummary.RetryExhaustedFailureCount,
            FailureDiagnosticDroppedTotal = state.FailureDiagnosticDroppedTotal,
            InFlightSource = state.InFlightObservation?.Source?.Clone(),
            ActiveMaterializationRoute = state.ActiveMaterializationRoute?.Clone(),
            MaterializationCutover = state.MaterializationCutover?.Clone(),
            StatusRoute = state.StatusRoute?.Clone(),
        };
        document.OldestUnresolvedFailureAtUtc = failureSummary.OldestUnresolvedFailureAtUtc?.Clone();
        document.RecentObservedEnvelopes.Add(state.RecentObservedEnvelopes);
        document.LastSuccessfulSourceCoordinates.Add(
            state.LastSuccessfulSourceCoordinatesByActor.Values
                .OrderBy(static source => source.ActorId, StringComparer.Ordinal)
                .Select(static source => source.Clone()));
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

        return document;
    }
}
