using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptRuntimeGAgent
{
    protected override ScriptRuntimeState TransitionState(ScriptRuntimeState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<ScriptDefinitionQueryQueuedEvent>(ApplyQueued)
            .On<ScriptDefinitionQueryClearedEvent>(ApplyCleared)
            .On<ScriptRunDomainEventCommitted>(ApplyCommitted)
            .OrCurrent();

    private static ScriptRuntimeState ApplyQueued(
        ScriptRuntimeState state,
        ScriptDefinitionQueryQueuedEvent queued)
    {
        var next = state.Clone();
        next.PendingDefinitionQueries[queued.RequestId] = new PendingScriptDefinitionQueryState
        {
            RequestId = queued.RequestId ?? string.Empty,
            RunEvent = queued.RunEvent?.Clone() ?? new RunScriptRequestedEvent(),
            QueuedAtUnixTimeMs = queued.QueuedAtUnixTimeMs,
            TimeoutCallbackId = queued.TimeoutCallbackId ?? string.Empty,
        };
        return next;
    }

    private static ScriptRuntimeState ApplyCleared(
        ScriptRuntimeState state,
        ScriptDefinitionQueryClearedEvent cleared)
    {
        var next = state.Clone();
        if (!string.IsNullOrWhiteSpace(cleared.RequestId))
            next.PendingDefinitionQueries.Remove(cleared.RequestId);
        return next;
    }

    private static ScriptRuntimeState ApplyCommitted(
        ScriptRuntimeState state,
        ScriptRunDomainEventCommitted committed)
    {
        var next = state.Clone();
        if (!string.Equals(committed.EventType, RunFailedEventType, StringComparison.Ordinal))
        {
            next.DefinitionActorId = committed.DefinitionActorId ?? string.Empty;
            next.Revision = committed.ScriptRevision ?? string.Empty;
        }

        next.LastRunId = committed.RunId ?? string.Empty;
        CopyPayloads(committed.StatePayloads, next.StatePayloads);
        CopyPayloads(committed.ReadModelPayloads, next.ReadModelPayloads);
        next.LastAppliedSchemaVersion = committed.ReadModelSchemaVersion ?? string.Empty;
        next.LastSchemaHash = committed.ReadModelSchemaHash ?? string.Empty;
        next.LastAppliedEventVersion = state.LastAppliedEventVersion + 1;
        next.LastEventId = committed.RunId ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(committed.RunId))
        {
            var pendingKeys = next.PendingDefinitionQueries
                .Where(x => string.Equals(x.Value.RunEvent?.RunId, committed.RunId, StringComparison.Ordinal))
                .Select(x => x.Key)
                .ToArray();
            foreach (var pendingKey in pendingKeys)
                next.PendingDefinitionQueries.Remove(pendingKey);
        }

        return next;
    }
}
