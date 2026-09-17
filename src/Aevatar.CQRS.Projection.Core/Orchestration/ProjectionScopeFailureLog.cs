using Aevatar.CQRS.Projection.Core.Abstractions.Orchestration;
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
            SourceActorId = ResolveSourceActorId(envelope),
        };
    }

    private static string ResolveSourceActorId(EventEnvelope envelope)
    {
        var sourceActorId = CommittedStateEventEnvelope.GetOriginActorId(envelope);
        return string.IsNullOrWhiteSpace(sourceActorId)
            ? envelope.Route?.PublisherActorId ?? string.Empty
            : sourceActorId;
    }

    public static IReadOnlyList<ProjectionScopeFailure> GetPendingFailures(
        ProjectionScopeState state,
        int maxItems,
        bool includeRetryExhausted = true)
    {
        return state.Failures
            .Where(failure => includeRetryExhausted || !failure.RetryExhausted)
            .Take(Math.Max(1, maxItems))
            .ToList();
    }

    public static ProjectionScopeFailureSummary BuildSummary(
        IEnumerable<ProjectionScopeFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        var materialized = failures as IReadOnlyCollection<ProjectionScopeFailure> ?? failures.ToArray();
        var oldest = materialized
            .Where(static failure => failure.OccurredAtUtc != null)
            .OrderBy(static failure => failure.OccurredAtUtc)
            .FirstOrDefault();
        return new ProjectionScopeFailureSummary
        {
            UnresolvedFailureCount = materialized.Count,
            RetryExhaustedFailureCount = materialized.Count(static failure => failure.RetryExhausted),
            OldestUnresolvedFailureAtUtc = oldest?.OccurredAtUtc?.Clone(),
        };
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
