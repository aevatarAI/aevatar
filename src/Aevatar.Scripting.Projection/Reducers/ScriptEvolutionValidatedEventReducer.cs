using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.ReadModels;

namespace Aevatar.Scripting.Projection.Reducers;

public sealed class ScriptEvolutionValidatedEventReducer
    : ScriptEvolutionEventReducerBase<ScriptEvolutionValidatedEvent>
{
    protected override bool ReduceTyped(
        ScriptEvolutionReadModel readModel,
        ScriptEvolutionSessionProjectionContext context,
        EventEnvelope envelope,
        ScriptEvolutionValidatedEvent evt,
        DateTimeOffset now)
    {
        _ = context;

        readModel.ProposalId = evt.ProposalId ?? string.Empty;
        readModel.ScriptId = evt.ScriptId ?? string.Empty;
        readModel.CandidateRevision = evt.CandidateRevision ?? string.Empty;
        readModel.ValidationStatus = evt.IsValid
            ? ScriptEvolutionStatuses.Validated
            : ScriptEvolutionStatuses.ValidationFailed;
        readModel.Diagnostics.Clear();
        readModel.Diagnostics.AddRange(evt.Diagnostics);
        readModel.LastEventId = envelope.Id ?? string.Empty;
        readModel.UpdatedAt = now;
        return true;
    }
}
