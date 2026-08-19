using Google.Protobuf.Collections;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeStateApplier
{
    private const int RecentObservedEnvelopeLimit = 50;

    public static ProjectionScopeState ApplyStarted(ProjectionScopeState current, ProjectionScopeStartedEvent evt)
    {
        var next = current.Clone();
        next.RootActorId = evt.RootActorId;
        next.ProjectionKind = evt.ProjectionKind;
        next.SessionId = evt.SessionId;
        next.Mode = evt.Mode;
        next.Active = true;
        next.Released = false;
        next.ActivationGeneration = evt.ActivationGeneration;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyActivationGenerationMigrated(
        ProjectionScopeState current,
        ProjectionScopeActivationGenerationMigratedEvent evt)
    {
        var next = current.Clone();
        next.ActivationGeneration = evt.ActivationGeneration;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyAttachmentUpdated(
        ProjectionScopeState current,
        ProjectionObservationAttachmentUpdatedEvent evt)
    {
        var next = current.Clone();
        next.ObservationAttached = evt.Attached;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyReleased(ProjectionScopeState current, ProjectionScopeReleasedEvent evt)
    {
        var next = current.Clone();
        next.Released = true;
        next.ObservationAttached = false;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyEnvelopeReceived(
        ProjectionScopeState current,
        ProjectionScopeEnvelopeReceivedEvent evt)
    {
        var next = current.Clone();
        next.ReceivedEnvelopeTotal += 1;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyEnvelopeAttempted(
        ProjectionScopeState current,
        ProjectionScopeEnvelopeAttemptedEvent evt)
    {
        var next = current.Clone();
        next.AttemptedEnvelopeTotal += 1;
        next.HighestSeenVersion = Math.Max(current.HighestSeenVersion, evt.HighestSeenVersion);
        if (!string.IsNullOrWhiteSpace(evt.SourceActorId) && evt.HighestSeenVersion > 0)
        {
            var previous = next.HighestSeenVersionsByActor.TryGetValue(evt.SourceActorId, out var version)
                ? version
                : 0;
            next.HighestSeenVersionsByActor[evt.SourceActorId] = Math.Max(previous, evt.HighestSeenVersion);
        }
        if (evt.ObservedEnvelope != null)
        {
            next.RecentObservedEnvelopes.Add(evt.ObservedEnvelope.Clone());
            while (next.RecentObservedEnvelopes.Count > RecentObservedEnvelopeLimit)
                next.RecentObservedEnvelopes.RemoveAt(0);
        }
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyObservationStaged(
        ProjectionScopeState current,
        ProjectionScopeObservationStagedEvent evt)
    {
        var next = current.Clone();
        next.InFlightObservation = evt.Observation?.Clone();
        next.UpdatedAtUtc = evt.Observation?.StagedAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyWatermarkAdvanced(
        ProjectionScopeState current,
        ProjectionScopeWatermarkAdvancedEvent evt)
    {
        var next = current.Clone();
        next.SuccessfulMaterializationTotal += 1;
        next.LastSuccessfulVersion = Math.Max(current.LastSuccessfulVersion, evt.LastSuccessfulVersion);
        if (!string.IsNullOrWhiteSpace(evt.SourceActorId) && evt.LastSuccessfulVersion > 0)
        {
            var previous = next.LastSuccessfulVersionsByActor.TryGetValue(evt.SourceActorId, out var version)
                ? version
                : 0;
            next.LastSuccessfulVersionsByActor[evt.SourceActorId] = Math.Max(previous, evt.LastSuccessfulVersion);
        }
        var coordinate = BuildSourceCoordinate(evt);
        if (coordinate != null)
        {
            if (HasSameSource(next.InFlightObservation?.Source, coordinate))
                next.InFlightObservation = null;

            if (!next.LastSuccessfulSourceCoordinatesByActor.TryGetValue(coordinate.ActorId, out var previous) ||
                coordinate.StateVersion >= previous.StateVersion)
            {
                next.LastSuccessfulSourceCoordinatesByActor[coordinate.ActorId] = coordinate;
            }
        }
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyDispatchFailed(
        ProjectionScopeState current,
        ProjectionScopeDispatchFailedEvent evt)
    {
        var next = current.Clone();
        next.Failures.Add(new ProjectionScopeFailure
        {
            FailureId = evt.FailureId,
            Stage = evt.Stage,
            EventId = evt.EventId,
            EventType = evt.EventType,
            SourceVersion = evt.SourceVersion,
            Reason = evt.Reason,
            Envelope = evt.Envelope?.Clone(),
            Attempts = 0,
            OccurredAtUtc = evt.OccurredAtUtc?.Clone(),
            SourceActorId = evt.SourceActorId,
        });
        next.FailureSummary = ProjectionScopeFailureLog.BuildSummary(next.Failures);
        next.RetainedFailureDiagnostics.Add(new ProjectionFailureDiagnostic
        {
            FailureId = evt.FailureId,
            Stage = evt.Stage,
            EventId = evt.EventId,
            EventType = evt.EventType,
            SourceVersion = evt.SourceVersion,
            OccurredAtUtc = evt.OccurredAtUtc?.Clone(),
            SourceActorId = evt.SourceActorId,
        });
        var dropped = ProjectionFailureRetentionPolicy.Trim(next.RetainedFailureDiagnostics);
        next.FailureDiagnosticDroppedTotal += dropped.Count;
        next.FailedAttemptTotal += 1;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyFailureReplayed(
        ProjectionScopeState current,
        ProjectionScopeFailureReplayedEvent evt)
    {
        var next = current.Clone();
        var existing = next.Failures.FirstOrDefault(x => string.Equals(x.FailureId, evt.FailureId, StringComparison.Ordinal));
        if (existing == null)
            return next;

        if (evt.Succeeded)
        {
            next.Failures.Remove(existing);
        }
        else
        {
            existing.Attempts += 1;
            existing.Reason = evt.Reason ?? existing.Reason;
            next.FailedAttemptTotal += 1;
            if (!existing.RetryExhausted &&
                existing.Attempts >= ProjectionFailureRetentionPolicy.DefaultMaxReplayAttempts)
            {
                existing.RetryExhausted = true;
                next.RetryExhaustedTotal += 1;
            }
        }

        next.FailureSummary = ProjectionScopeFailureLog.BuildSummary(next.Failures);

        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyAutomaticRecoveryRequested(
        ProjectionScopeState current,
        ProjectionScopeAutomaticRecoveryRequestedEvent evt)
    {
        var next = current.Clone();
        next.LastAutomaticRecoveryObservedStateVersion = Math.Max(
            next.LastAutomaticRecoveryObservedStateVersion,
            evt.ObservedScopeStateVersion);
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyMaterializationRouteInitialized(
        ProjectionScopeState current,
        ProjectionMaterializationRouteInitializedEvent evt)
    {
        var next = current.Clone();
        next.ActiveMaterializationRoute = evt.Route?.Clone();
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyMaterializationCutoverRequested(
        ProjectionScopeState current,
        ProjectionMaterializationCutoverRequestedEvent evt)
    {
        var next = current.Clone();
        next.MaterializationCutover = new ProjectionMaterializationCutoverState
        {
            Phase = ProjectionMaterializationCutoverPhase.Requested,
            CandidateRoute = evt.CandidateRoute?.Clone(),
            UpdatedAtUtc = evt.OccurredAtUtc?.Clone(),
        };
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyMaterializationCutoverCandidateBuilt(
        ProjectionScopeState current,
        ProjectionMaterializationCutoverCandidateBuiltEvent evt) =>
        ApplyCutoverProgress(
            current,
            ProjectionMaterializationCutoverPhase.CandidateBuilt,
            evt.CandidateRoute,
            evt.CandidateSource,
            evt.CandidateFingerprint,
            evt.OccurredAtUtc);

    public static ProjectionScopeState ApplyMaterializationCutoverGoldenVerified(
        ProjectionScopeState current,
        ProjectionMaterializationCutoverGoldenVerifiedEvent evt) =>
        ApplyCutoverProgress(
            current,
            ProjectionMaterializationCutoverPhase.GoldenVerified,
            evt.CandidateRoute,
            evt.CandidateSource,
            evt.CandidateFingerprint,
            evt.OccurredAtUtc);

    public static ProjectionScopeState ApplyMaterializationCutoverActivated(
        ProjectionScopeState current,
        ProjectionMaterializationCutoverActivatedEvent evt)
    {
        if (evt.Route == null || evt.Route.RouteEpoch <= 0)
            throw new InvalidOperationException("An activated materialization route requires a positive route epoch.");
        if (current.ActiveMaterializationRoute != null &&
            evt.Route.RouteEpoch <= current.ActiveMaterializationRoute.RouteEpoch)
        {
            throw new InvalidOperationException(
                "A materialization route activation must advance the actor-owned route epoch.");
        }
        var cutover = current.MaterializationCutover;
        if (cutover?.Phase != ProjectionMaterializationCutoverPhase.GoldenVerified ||
            !HasSameRoute(cutover.CandidateRoute, evt.Route) ||
            !HasSameSource(cutover.CandidateSource, evt.Source) ||
            !string.Equals(
                cutover.CandidateFingerprint,
                evt.CandidateFingerprint,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(evt.CandidateFingerprint) ||
            evt.ActivationProof == null)
        {
            throw new InvalidOperationException(
                "A materialization route can activate only from matching golden-verified candidate evidence.");
        }

        var next = ApplyCutoverProgress(
            current,
            ProjectionMaterializationCutoverPhase.Activated,
            evt.Route,
            evt.Source,
            evt.CandidateFingerprint,
            evt.OccurredAtUtc);
        next.ActiveMaterializationRoute = evt.Route?.Clone();
        next.MaterializationCutover.ActivationProof = evt.ActivationProof?.Clone();
        return next;
    }

    private static bool HasSameRoute(
        ProjectionMaterializationRouteFingerprint? left,
        ProjectionMaterializationRouteFingerprint? right) =>
        left != null &&
        right != null &&
        left.RouteEpoch == right.RouteEpoch &&
        left.ContractVersion == right.ContractVersion &&
        string.Equals(left.ContractId, right.ContractId, StringComparison.Ordinal) &&
        string.Equals(left.PhysicalNamespace, right.PhysicalNamespace, StringComparison.Ordinal);

    private static ProjectionScopeState ApplyCutoverProgress(
        ProjectionScopeState current,
        ProjectionMaterializationCutoverPhase phase,
        ProjectionMaterializationRouteFingerprint? route,
        ProjectionSourceCoordinate? source,
        string? candidateFingerprint,
        Google.Protobuf.WellKnownTypes.Timestamp? occurredAtUtc)
    {
        var next = current.Clone();
        next.MaterializationCutover = new ProjectionMaterializationCutoverState
        {
            Phase = phase,
            CandidateRoute = route?.Clone(),
            CandidateSource = source?.Clone(),
            CandidateFingerprint = candidateFingerprint ?? string.Empty,
            UpdatedAtUtc = occurredAtUtc?.Clone(),
        };
        next.UpdatedAtUtc = occurredAtUtc?.Clone();
        return next;
    }

    private static ProjectionSourceCoordinate? BuildSourceCoordinate(
        ProjectionScopeWatermarkAdvancedEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SourceActorId) ||
            evt.ObservedEnvelope == null ||
            evt.ObservedEnvelope.StateVersion <= 0 ||
            string.IsNullOrWhiteSpace(evt.ObservedEnvelope.EventId))
        {
            return null;
        }

        return new ProjectionSourceCoordinate
        {
            ActorId = evt.SourceActorId,
            StateVersion = evt.ObservedEnvelope.StateVersion,
            EventId = evt.ObservedEnvelope.EventId,
        };
    }

    private static bool HasSameSource(
        ProjectionSourceCoordinate? left,
        ProjectionSourceCoordinate? right) =>
        left != null &&
        right != null &&
        left.StateVersion == right.StateVersion &&
        string.Equals(left.ActorId, right.ActorId, StringComparison.Ordinal) &&
        string.Equals(left.EventId, right.EventId, StringComparison.Ordinal);

    /// <summary>
    /// Phase 1: a warming route at a strictly higher epoch replaces the current route (epoch
    /// fence); the previous writer's release flag is reset for the new epoch.
    /// </summary>
    public static ProjectionScopeState ApplyStatusRouteWarmingStarted(
        ProjectionScopeState current,
        ProjectionScopeStatusRouteWarmingStartedEvent evt)
    {
        var route = evt.Route;
        if (route == null || route.RouteEpoch <= (current.StatusRoute?.RouteEpoch ?? 0))
            return current;

        var next = current.Clone();
        next.StatusRoute = route.Clone();
        next.StatusRoute.Phase = ProjectionScopeStatusRoutePhase.Warming;
        next.StatusRoute.LegacyRouteReleased = false;
        next.StatusRoute.CaughtUpVersion = 0;
        next.StatusRoute.FlipVersion = 0;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyStatusRouteCaughtUp(
        ProjectionScopeState current,
        ProjectionScopeStatusRouteCaughtUpEvent evt)
    {
        var route = current.StatusRoute;
        if (route == null ||
            route.RouteEpoch != evt.RouteEpoch ||
            route.Phase != ProjectionScopeStatusRoutePhase.Warming ||
            evt.ObservedVersion <= route.CaughtUpVersion)
        {
            return current;
        }

        var next = current.Clone();
        next.StatusRoute.CaughtUpVersion = evt.ObservedVersion;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyStatusRouteBlocked(
        ProjectionScopeState current,
        ProjectionScopeStatusRouteBlockedEvent evt)
    {
        var route = current.StatusRoute;
        if (route == null ||
            route.RouteEpoch != evt.RouteEpoch ||
            route.Phase != ProjectionScopeStatusRoutePhase.Warming)
        {
            return current;
        }

        var next = current.Clone();
        next.StatusRoute.Phase = ProjectionScopeStatusRoutePhase.Blocked;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    /// <summary>
    /// Phase 5 (flip) for the current epoch's warming/blocked route, or a direct activation at
    /// a strictly higher epoch (a phase-less route written by an earlier binary); a stale or
    /// replayed activation at a lower epoch never moves the route.
    /// </summary>
    public static ProjectionScopeState ApplyStatusRouteActivated(
        ProjectionScopeState current,
        ProjectionScopeStatusRouteActivatedEvent evt)
    {
        var route = evt.Route;
        if (route == null)
            return current;

        var currentEpoch = current.StatusRoute?.RouteEpoch ?? 0;
        var flipsCurrentCutover =
            route.RouteEpoch == currentEpoch &&
            current.StatusRoute!.Phase is ProjectionScopeStatusRoutePhase.Warming
                or ProjectionScopeStatusRoutePhase.Blocked;
        if (route.RouteEpoch < currentEpoch || (route.RouteEpoch == currentEpoch && !flipsCurrentCutover))
            return current; // epoch fence

        var next = current.Clone();
        next.StatusRoute = route.Clone();
        if (next.StatusRoute.Phase == ProjectionScopeStatusRoutePhase.Unspecified ||
            next.StatusRoute.Phase == ProjectionScopeStatusRoutePhase.Warming ||
            next.StatusRoute.Phase == ProjectionScopeStatusRoutePhase.Blocked)
        {
            next.StatusRoute.Phase = ProjectionScopeStatusRoutePhase.Active;
        }

        if (!flipsCurrentCutover)
            next.StatusRoute.LegacyRouteReleased = false;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }

    public static ProjectionScopeState ApplyStatusLegacyRouteReleased(
        ProjectionScopeState current,
        ProjectionScopeStatusLegacyRouteReleasedEvent evt)
    {
        if (current.StatusRoute == null || current.StatusRoute.RouteEpoch != evt.RouteEpoch)
            return current;

        var next = current.Clone();
        next.StatusRoute.LegacyRouteReleased = true;
        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }
}
