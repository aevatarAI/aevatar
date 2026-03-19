using Google.Protobuf.Collections;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeStateApplier
{
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

    public static ProjectionScopeState ApplyWatermarkAdvanced(
        ProjectionScopeState current,
        ProjectionScopeWatermarkAdvancedEvent evt)
    {
        var next = current.Clone();
        next.LastObservedVersion = Math.Max(current.LastObservedVersion, evt.LastObservedVersion);
        next.LastSuccessfulVersion = Math.Max(current.LastSuccessfulVersion, evt.LastSuccessfulVersion);
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
        });
        ProjectionFailureRetentionPolicy.Trim(next.Failures);
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
        }

        next.UpdatedAtUtc = evt.OccurredAtUtc?.Clone();
        return next;
    }
}
