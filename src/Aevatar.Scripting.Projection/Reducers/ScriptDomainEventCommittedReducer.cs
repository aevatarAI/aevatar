using Aevatar.Scripting.Core;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Reducers;

public sealed class ScriptDomainEventCommittedReducer
    : ScriptEventReducerBase<ScriptDomainEventCommitted>
{
    protected override bool ReduceTyped(
        ScriptExecutionReadModel readModel,
        ScriptProjectionContext context,
        EventEnvelope envelope,
        ScriptDomainEventCommitted evt,
        DateTimeOffset now)
    {
        _ = now;
        readModel.Id = context.RootActorId;
        readModel.ScriptId = string.IsNullOrWhiteSpace(evt.ScriptId) ? context.ScriptId : evt.ScriptId;
        readModel.Revision = evt.ScriptRevision ?? string.Empty;
        readModel.LastRunId = evt.RunId ?? string.Empty;
        readModel.LastEventType = evt.EventType ?? string.Empty;
        readModel.StatePayloadJson = evt.PayloadJson ?? string.Empty;
        readModel.StateVersion += 1;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        return true;
    }
}
