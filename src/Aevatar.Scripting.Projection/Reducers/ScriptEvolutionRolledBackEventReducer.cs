using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Reducers;

public sealed class ScriptEvolutionRolledBackEventReducer
    : ScriptEvolutionEventReducerBase<ScriptEvolutionRolledBackEvent>
{
    protected override bool ReduceTyped(
        ScriptEvolutionReadModel readModel,
        ScriptEvolutionSessionProjectionContext context,
        EventEnvelope envelope,
        ScriptEvolutionRolledBackEvent evt,
        DateTimeOffset now)
    {
        _ = context;

        readModel.ProposalId = evt.ProposalId ?? string.Empty;
        readModel.ScriptId = evt.ScriptId ?? string.Empty;
        readModel.CandidateRevision = evt.TargetRevision ?? string.Empty;
        readModel.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        readModel.RollbackStatus = ScriptEvolutionStatuses.RolledBack;
        readModel.PromotionStatus = ScriptEvolutionStatuses.RolledBack;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        readModel.UpdatedAt = now;
        return true;
    }
}
