using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Workflow.Core.Modules;

internal static class WorkflowToolCallAttemptPersistence
{
    internal static IReadOnlyList<WorkflowToolCallAttemptPersistenceFact> BuildNewFacts(
        ToolCallModuleState? authoritative,
        ToolCallModuleState incoming,
        string scopeId,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        var existing = (authoritative?.PendingExecutions.Values ?? [])
            .Select(TryBuildIdentity)
            .Where(static identity => identity.HasValue)
            .Select(static identity => identity!.Value)
            .ToHashSet();
        var observedAt = Timestamp.FromDateTimeOffset(observedAtUtc);

        return incoming.PendingExecutions.Values
            .Select(pending => (Pending: pending, Identity: TryBuildIdentity(pending)))
            .Where(static candidate => candidate.Identity.HasValue)
            .Where(candidate => !existing.Contains(candidate.Identity!.Value))
            .GroupBy(static candidate => candidate.Identity!.Value)
            .Select(static group => group
                .OrderBy(static candidate => candidate.Pending.AttemptPreparationStartedAtUtc?.Seconds ?? long.MaxValue)
                .ThenBy(static candidate => candidate.Pending.AttemptPreparationStartedAtUtc?.Nanos ?? int.MaxValue)
                .First())
            .OrderBy(static candidate => candidate.Identity!.Value.RunId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Identity!.Value.StepId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Identity!.Value.CallId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Identity!.Value.ExecutionId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Identity!.Value.ContinuationId, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Identity!.Value.Attempt)
            .Select(candidate => BuildFact(candidate.Pending, scopeId, observedAt, observedAtUtc))
            .ToArray();
    }

    internal static IReadOnlyList<WorkflowToolCallAttemptTimingObservation> BuildCommittedObservations(
        StateEvent committedEvent)
    {
        ArgumentNullException.ThrowIfNull(committedEvent);
        if (committedEvent.EventData?.Is(WorkflowExecutionStateUpsertedEvent.Descriptor) != true)
            return [];

        var upserted = committedEvent.EventData.Unpack<WorkflowExecutionStateUpsertedEvent>();
        return upserted.ToolCallAttemptPersistenceFacts
            .OrderBy(static fact => Normalize(fact.RunId), StringComparer.Ordinal)
            .ThenBy(static fact => Normalize(fact.StepId), StringComparer.Ordinal)
            .ThenBy(static fact => Normalize(fact.CallId), StringComparer.Ordinal)
            .ThenBy(static fact => Normalize(fact.ExecutionId), StringComparer.Ordinal)
            .ThenBy(static fact => Normalize(fact.ContinuationId), StringComparer.Ordinal)
            .ThenBy(static fact => fact.Attempt)
            .Select(fact => new WorkflowToolCallAttemptTimingObservation
            {
                ScopeId = Normalize(fact.ScopeId),
                RunId = Normalize(fact.RunId),
                StepId = Normalize(fact.StepId),
                CallId = Normalize(fact.CallId),
                ExecutionId = Normalize(fact.ExecutionId),
                ContinuationId = Normalize(fact.ContinuationId),
                Attempt = Math.Max(0, fact.Attempt),
                Waterline = WorkflowToolCallAttemptWaterline.PendingStatePersisted,
                ObservedAtUtc = fact.ObservedAtUtc?.Clone(),
                PreparationElapsedMs = Math.Max(0, fact.PreparationElapsedMs),
                CommittedEventId = Normalize(committedEvent.EventId),
                CommittedStateVersion = Math.Max(0, committedEvent.Version),
            })
            .ToArray();
    }

    private static WorkflowToolCallAttemptPersistenceFact BuildFact(
        PendingToolCallExecutionState pending,
        string scopeId,
        Timestamp observedAt,
        DateTimeOffset observedAtUtc) =>
        new()
        {
            ScopeId = Normalize(scopeId),
            RunId = Normalize(pending.RunId),
            StepId = Normalize(pending.StepId),
            CallId = Normalize(pending.CallId),
            ExecutionId = Normalize(pending.ExecutionId),
            ContinuationId = Normalize(pending.ContinuationId),
            Attempt = Math.Max(1, pending.Attempt),
            ObservedAtUtc = observedAt.Clone(),
            PreparationElapsedMs = PreparationElapsedMilliseconds(pending, observedAtUtc),
        };

    private static long PreparationElapsedMilliseconds(
        PendingToolCallExecutionState pending,
        DateTimeOffset observedAtUtc)
    {
        if (pending.AttemptPreparationStartedAtUtc == null)
            return 0;

        try
        {
            return Math.Max(
                0,
                (long)Math.Ceiling(
                    (observedAtUtc - pending.AttemptPreparationStartedAtUtc.ToDateTimeOffset()).TotalMilliseconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0;
        }
    }

    private static AttemptIdentity? TryBuildIdentity(PendingToolCallExecutionState pending)
    {
        var identity = new AttemptIdentity(
            Normalize(pending.RunId),
            Normalize(pending.StepId),
            Normalize(pending.CallId),
            Normalize(pending.ExecutionId),
            Normalize(pending.ContinuationId),
            pending.Attempt);
        return identity.IsComplete ? identity : null;
    }

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private readonly record struct AttemptIdentity(
        string RunId,
        string StepId,
        string CallId,
        string ExecutionId,
        string ContinuationId,
        int Attempt)
    {
        internal bool IsComplete =>
            RunId.Length > 0 &&
            StepId.Length > 0 &&
            CallId.Length > 0 &&
            ExecutionId.Length > 0 &&
            ContinuationId.Length > 0 &&
            Attempt > 0;
    }
}
