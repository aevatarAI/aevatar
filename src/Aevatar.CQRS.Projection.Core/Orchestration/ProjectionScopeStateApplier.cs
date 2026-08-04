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

        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }
}
