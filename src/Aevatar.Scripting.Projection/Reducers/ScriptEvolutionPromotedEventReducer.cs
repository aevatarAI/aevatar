using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Reducers;

public sealed class ScriptEvolutionPromotedEventReducer
    : ScriptEvolutionEventReducerBase<ScriptEvolutionPromotedEvent>
{
    protected override bool ReduceTyped(
        ScriptEvolutionReadModel readModel,
        ScriptEvolutionProjectionContext context,
        EventEnvelope envelope,
        ScriptEvolutionPromotedEvent evt,
        DateTimeOffset now)
    {
        _ = context;

        readModel.ProposalId = evt.ProposalId ?? string.Empty;
        readModel.ScriptId = evt.ScriptId ?? string.Empty;
        readModel.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        readModel.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        readModel.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        readModel.PromotionStatus = ScriptEvolutionStatuses.Promoted;
        readModel.FailureReason = string.Empty;
        readModel.LastEventId = envelope.Id ?? string.Empty;
        readModel.UpdatedAt = now;
        return true;
    }
}
