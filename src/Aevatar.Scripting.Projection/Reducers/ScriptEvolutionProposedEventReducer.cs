using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Reducers;

public sealed class ScriptEvolutionProposedEventReducer
    : ScriptEvolutionEventReducerBase<ScriptEvolutionProposedEvent>
{
    protected override bool ReduceTyped(
        ScriptEvolutionReadModel readModel,
        ScriptEvolutionProjectionContext context,
        EventEnvelope envelope,
        ScriptEvolutionProposedEvent evt,
        DateTimeOffset now)
    {
        readModel.Id = context.RootActorId;
        readModel.ProposalId = evt.ProposalId ?? string.Empty;
        readModel.ScriptId = evt.ScriptId ?? string.Empty;
        readModel.BaseRevision = evt.BaseRevision ?? string.Empty;
        readModel.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        readModel.DefinitionActorId = evt.DefinitionActorId ?? string.Empty;
        readModel.CatalogActorId = evt.CatalogActorId ?? string.Empty;
        readModel.ValidationStatus = "pending";
        readModel.PromotionStatus = "pending";
        readModel.RollbackStatus = string.Empty;
        readModel.FailureReason = string.Empty;
        readModel.Diagnostics.Clear();
        readModel.LastEventId = envelope.Id ?? string.Empty;
        readModel.UpdatedAt = now;
        return true;
    }
}
