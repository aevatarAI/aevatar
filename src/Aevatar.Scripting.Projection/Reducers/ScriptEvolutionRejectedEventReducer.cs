using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Reducers;

public sealed class ScriptEvolutionRejectedEventReducer
    : ScriptEvolutionEventReducerBase<ScriptEvolutionRejectedEvent>
{
    protected override bool ReduceTyped(
        ScriptEvolutionReadModel readModel,
        ScriptEvolutionProjectionContext context,
        EventEnvelope envelope,
        ScriptEvolutionRejectedEvent evt,
        DateTimeOffset now)
    {
        _ = context;

        readModel.ProposalId = evt.ProposalId ?? string.Empty;
        readModel.ScriptId = evt.ScriptId ?? string.Empty;
        readModel.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        readModel.PromotionStatus = "rejected";
        readModel.FailureReason = evt.FailureReason ?? string.Empty;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        readModel.UpdatedAt = now;
        return true;
    }
}
