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
        var eventType = evt.EventType ?? string.Empty;
        if (string.Equals(eventType, "ClaimManualReviewRequestedEvent", StringComparison.Ordinal))
        {
            readModel.DecisionStatus = "ManualReview";
            readModel.ManualReviewRequired = true;
        }
        else if (string.Equals(eventType, "ClaimApprovedEvent", StringComparison.Ordinal))
        {
            readModel.DecisionStatus = "Approved";
            readModel.ManualReviewRequired = false;
        }
        else if (string.Equals(eventType, "ClaimRejectedEvent", StringComparison.Ordinal))
        {
            readModel.DecisionStatus = "Rejected";
            readModel.ManualReviewRequired = false;
        }
        readModel.StatePayloadJson = string.IsNullOrWhiteSpace(evt.StatePayloadJson)
            ? evt.PayloadJson ?? string.Empty
            : evt.StatePayloadJson;
        readModel.StateVersion += 1;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        return true;
    }
}
