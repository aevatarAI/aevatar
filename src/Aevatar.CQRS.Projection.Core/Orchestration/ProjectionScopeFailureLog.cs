using Google.Protobuf.WellKnownTypes;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeFailureLog
{
    public static ProjectionScopeDispatchFailedEvent BuildFailureEvent(
        string stage,
        string eventId,
        string eventType,
        long sourceVersion,
        string reason,
        EventEnvelope envelope)
    {
        return new ProjectionScopeDispatchFailedEvent
        {
            FailureId = Guid.NewGuid().ToString("N"),
            Stage = stage,
            EventId = eventId,
            EventType = eventType,
            SourceVersion = sourceVersion,
            Reason = reason,
            Envelope = envelope.Clone(),
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
    }

    public static IReadOnlyList<ProjectionScopeFailure> GetPendingFailures(
        ProjectionScopeState state,
        int maxItems)
    {
        return state.Failures
            .Take(Math.Max(1, maxItems))
            .ToList();
    }

    public static ProjectionScopeFailureReplayedEvent BuildReplayResultEvent(
        string failureId,
        bool succeeded,
        string? reason = null)
    {
        return new ProjectionScopeFailureReplayedEvent
        {
            FailureId = failureId,
            Succeeded = succeeded,
            Reason = reason ?? string.Empty,
            OccurredAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        };
    }
}
