using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionDurableCompletionResolver
    : ICommandDurableCompletionResolver<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion>
{
    private readonly IScriptEvolutionDecisionFallbackPort _decisionFallbackPort;

    public ScriptEvolutionDurableCompletionResolver(
        IScriptEvolutionDecisionFallbackPort decisionFallbackPort)
    {
        _decisionFallbackPort = decisionFallbackPort ?? throw new ArgumentNullException(nameof(decisionFallbackPort));
    }

    public async Task<CommandDurableCompletionObservation<ScriptEvolutionInteractionCompletion>> ResolveAsync(
        ScriptEvolutionAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var decision = await _decisionFallbackPort.TryResolveAsync(
            receipt.SessionActorId,
            receipt.ProposalId,
            ct);
        if (decision == null)
        {
            throw new TimeoutException(
                $"Timeout waiting for script evolution session completion. proposal_id={receipt.ProposalId}");
        }

        return new CommandDurableCompletionObservation<ScriptEvolutionInteractionCompletion>(
            HasTerminalCompletion: true,
            Completion: ScriptEvolutionInteractionCompletion.FromDecision(decision));
    }
}
