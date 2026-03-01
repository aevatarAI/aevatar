using Aevatar.Scripting.Core;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Reducers;

public sealed class ScriptRunDomainEventCommittedReducer
    : ScriptEventReducerBase<ScriptRunDomainEventCommitted>
{
    protected override bool ReduceTyped(
        ScriptExecutionReadModel readModel,
        ScriptProjectionContext context,
        EventEnvelope envelope,
        ScriptRunDomainEventCommitted evt,
        DateTimeOffset now)
    {
        _ = now;

        readModel.Id = context.RootActorId;
        readModel.ScriptId = context.ScriptId;
        readModel.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        readModel.Revision = evt.ScriptRevision ?? string.Empty;
        readModel.ReadModelSchemaVersion = evt.ReadModelSchemaVersion ?? string.Empty;
        readModel.ReadModelSchemaHash = evt.ReadModelSchemaHash ?? string.Empty;
        readModel.LastRunId = evt.RunId ?? string.Empty;
        readModel.LastEventType = evt.EventType ?? string.Empty;
        readModel.LastDomainEventPayload = evt.Payload?.Clone();
        readModel.StatePayloads = evt.StatePayloads
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Clone(),
                StringComparer.Ordinal);
        readModel.ReadModelPayloads = evt.ReadModelPayloads
            .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
            .ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Clone(),
                StringComparer.Ordinal);

        readModel.StateVersion += 1;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        return true;
    }
}
