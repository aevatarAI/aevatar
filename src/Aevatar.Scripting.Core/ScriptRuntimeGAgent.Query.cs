using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Scripting.Abstractions;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptRuntimeGAgent
{
    [EventHandler]
    public Task HandleQueryScriptRuntimeSnapshotRequested(QueryScriptRuntimeSnapshotRequestedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RequestId) || string.IsNullOrWhiteSpace(evt.ReplyStreamId))
            return Task.CompletedTask;

        var found =
            !string.IsNullOrWhiteSpace(State.DefinitionActorId) ||
            !string.IsNullOrWhiteSpace(State.LastRunId) ||
            State.PendingDefinitionQueries.Count > 0;

        var responded = new ScriptRuntimeSnapshotRespondedEvent
        {
            RequestId = evt.RequestId,
            Found = found,
            RuntimeActorId = Id,
            DefinitionActorId = State.DefinitionActorId ?? string.Empty,
            Revision = State.Revision ?? string.Empty,
            LastRunId = State.LastRunId ?? string.Empty,
            LastEventType = State.LastEventType ?? string.Empty,
            LastDomainEventPayload = State.LastDomainEventPayload?.Clone(),
            StateVersion = State.LastAppliedEventVersion,
            LastEventId = State.LastEventId ?? string.Empty,
            ReadModelSchemaVersion = State.LastAppliedSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = State.LastSchemaHash ?? string.Empty,
            FailureReason = found
                ? string.Empty
                : $"Script runtime `{Id}` has not produced a durable snapshot yet.",
        };
        CopyPayloads(State.StatePayloads, responded.StatePayloads);
        CopyPayloads(State.ReadModelPayloads, responded.ReadModelPayloads);
        return EventPublisher.SendToAsync(evt.ReplyStreamId, responded, CancellationToken.None, sourceEnvelope: null);
    }
}
