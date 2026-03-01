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
        readModel.LastRunId = evt.RunId ?? string.Empty;
        readModel.LastEventType = evt.EventType ?? string.Empty;
        readModel.StatePayloadJson = evt.PayloadJson ?? string.Empty;
        readModel.StateVersion += 1;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        return true;
    }
}
