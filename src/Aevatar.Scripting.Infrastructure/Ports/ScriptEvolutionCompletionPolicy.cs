using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionCompletionPolicy
    : ICommandCompletionPolicy<ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion>
{
    public ScriptEvolutionInteractionCompletion IncompleteCompletion =>
        ScriptEvolutionInteractionCompletion.Pending;

    public bool TryResolve(
        ScriptEvolutionSessionCompletedEvent evt,
        out ScriptEvolutionInteractionCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(evt);

        completion = new ScriptEvolutionInteractionCompletion(
            Accepted: evt.Accepted,
            ProposalId: evt.ProposalId ?? string.Empty,
            Status: evt.Status ?? string.Empty,
            FailureReason: evt.FailureReason ?? string.Empty,
            DefinitionActorId: evt.DefinitionActorId ?? string.Empty,
            CatalogActorId: evt.CatalogActorId ?? string.Empty,
            ValidationReport: new ScriptEvolutionValidationReport(
                evt.Accepted,
                evt.Diagnostics.ToArray()));
        return true;
    }
}
