using Aevatar.Scripting.Core;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;
using System.Text.Json;

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
        readModel.LastDomainEventPayloadJson = evt.PayloadJson ?? string.Empty;

        readModel.StatePayloadJson = string.IsNullOrWhiteSpace(evt.StatePayloadJson)
            ? evt.PayloadJson ?? string.Empty
            : evt.StatePayloadJson;

        if (!string.IsNullOrWhiteSpace(evt.ReadModelPayloadJson))
        {
            readModel.ReadModelPayloadJson = evt.ReadModelPayloadJson;
            ApplyDecisionStatusFromReadModelPayload(readModel, evt.ReadModelPayloadJson);
        }
        else
        {
            ApplyDecisionStatusFromEventType(readModel, evt.EventType ?? string.Empty);
        }

        readModel.StateVersion += 1;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        return true;
    }

    private static void ApplyDecisionStatusFromReadModelPayload(
        ScriptExecutionReadModel readModel,
        string readModelPayloadJson)
    {
        try
        {
            using var json = JsonDocument.Parse(readModelPayloadJson);
            if (!json.RootElement.TryGetProperty("decision", out var decisionElement) ||
                decisionElement.ValueKind != JsonValueKind.String)
                return;

            var decision = decisionElement.GetString() ?? string.Empty;
            readModel.DecisionStatus = decision;
            readModel.ManualReviewRequired = decision.Contains("manual", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Ignore malformed read model payload and leave status untouched.
        }
    }

    private static void ApplyDecisionStatusFromEventType(
        ScriptExecutionReadModel readModel,
        string eventType)
    {
        if (string.Equals(eventType, "ClaimManualReviewRequestedEvent", StringComparison.Ordinal))
        {
            readModel.DecisionStatus = "ManualReview";
            readModel.ManualReviewRequired = true;
            return;
        }

        if (string.Equals(eventType, "ClaimApprovedEvent", StringComparison.Ordinal))
        {
            readModel.DecisionStatus = "Approved";
            readModel.ManualReviewRequired = false;
            return;
        }

        if (string.Equals(eventType, "ClaimRejectedEvent", StringComparison.Ordinal))
        {
            readModel.DecisionStatus = "Rejected";
            readModel.ManualReviewRequired = false;
        }
    }
}
