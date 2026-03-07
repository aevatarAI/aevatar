using Aevatar.Scripting.Core.Ports;
using Aevatar.Scripting.Core.Runtime;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace Aevatar.Scripting.Core;

public sealed partial class ScriptRuntimeGAgent
{
    private async Task PersistRunCommittedAsync(
        RunScriptRequestedEvent runEvent,
        ScriptDefinitionSnapshot snapshot,
        CancellationToken ct)
    {
        var committedEvents = await _orchestrator.ExecuteRunAsync(
            new ScriptRuntimeExecutionRequest(
                RuntimeActorId: Id,
                CurrentState: ClonePayloads(State.StatePayloads),
                CurrentReadModel: ClonePayloads(State.ReadModelPayloads),
                RunEvent: runEvent,
                ScriptId: snapshot.ScriptId,
                ScriptRevision: snapshot.Revision,
                SourceText: snapshot.SourceText,
                ReadModelSchemaVersion: snapshot.ReadModelSchemaVersion,
                ReadModelSchemaHash: snapshot.ReadModelSchemaHash),
            ct);
        await PersistDomainEventsAsync(committedEvents, ct);

        Logger.LogInformation(
            "Script run committed. runtime_actor_id={RuntimeActorId} run_id={RunId} committed_events={CommittedEvents} revision={Revision}",
            Id,
            runEvent.RunId,
            committedEvents.Count,
            snapshot.Revision);
    }

    private async Task PersistRunCommittedAsync(
        PendingScriptDefinitionQueryState pending,
        ScriptDefinitionSnapshot snapshot,
        CancellationToken ct)
    {
        var committedEvents = await _orchestrator.ExecuteRunAsync(
            new ScriptRuntimeExecutionRequest(
                RuntimeActorId: Id,
                CurrentState: ClonePayloads(State.StatePayloads),
                CurrentReadModel: ClonePayloads(State.ReadModelPayloads),
                RunEvent: pending.RunEvent,
                ScriptId: snapshot.ScriptId,
                ScriptRevision: snapshot.Revision,
                SourceText: snapshot.SourceText,
                ReadModelSchemaVersion: snapshot.ReadModelSchemaVersion,
                ReadModelSchemaHash: snapshot.ReadModelSchemaHash),
            ct);
        await PersistDomainEventsAsync(
            committedEvents.Concat<IMessage>([BuildDefinitionQueryClearedEvent(pending.RequestId)]),
            ct);

        Logger.LogInformation(
            "Script run committed. runtime_actor_id={RuntimeActorId} run_id={RunId} committed_events={CommittedEvents} revision={Revision}",
            Id,
            pending.RunEvent.RunId,
            committedEvents.Count,
            snapshot.Revision);
    }

    private async Task PersistRunFailureAsync(
        RunScriptRequestedEvent runEvent,
        string reason,
        CancellationToken ct)
    {
        await PersistDomainEventAsync(
            BuildRunFailureCommittedEvent(runEvent, reason),
            ct);
    }

    private async Task PersistPendingRunFailureAsync(
        PendingScriptDefinitionQueryState pending,
        string reason,
        CancellationToken ct)
    {
        await PersistDomainEventsAsync(
            [
                BuildRunFailureCommittedEvent(pending.RunEvent, reason),
                BuildDefinitionQueryClearedEvent(pending.RequestId),
            ],
            ct);
    }

    private async Task PersistPendingDefinitionQueryClearedAsync(
        string requestId,
        CancellationToken ct)
    {
        await PersistDomainEventAsync(
            BuildDefinitionQueryClearedEvent(requestId),
            ct);
    }

    private static ScriptDefinitionQueryClearedEvent BuildDefinitionQueryClearedEvent(string requestId) =>
        new()
        {
            RequestId = requestId ?? string.Empty,
        };

    private ScriptRunDomainEventCommitted BuildRunFailureCommittedEvent(
        RunScriptRequestedEvent runEvent,
        string reason)
    {
        var committed = new ScriptRunDomainEventCommitted
        {
            RunId = runEvent.RunId ?? string.Empty,
            ScriptRevision = string.IsNullOrWhiteSpace(runEvent.ScriptRevision) ? State.Revision : runEvent.ScriptRevision,
            DefinitionActorId = runEvent.DefinitionActorId ?? string.Empty,
            EventType = RunFailedEventType,
            Payload = Any.Pack(new StringValue
            {
                Value = string.IsNullOrWhiteSpace(reason) ? "Script run failed." : reason,
            }),
            ReadModelSchemaVersion = State.LastAppliedSchemaVersion ?? string.Empty,
            ReadModelSchemaHash = State.LastSchemaHash ?? string.Empty,
        };
        CopyPayloads(State.StatePayloads, committed.StatePayloads);
        CopyPayloads(State.ReadModelPayloads, committed.ReadModelPayloads);
        return committed;
    }
}
