using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class ScriptEvolutionDurableCompletionResolver
    : ICommandDurableCompletionResolver<ScriptEvolutionAcceptedReceipt, ScriptEvolutionInteractionCompletion>
{
    private readonly IScriptEvolutionDecisionReadPort _decisionReadPort;

    public ScriptEvolutionDurableCompletionResolver(
        IScriptEvolutionDecisionReadPort decisionReadPort)
    {
        _decisionReadPort = decisionReadPort ?? throw new ArgumentNullException(nameof(decisionReadPort));
    }

    public async Task<CommandDurableCompletionObservation<ScriptEvolutionInteractionCompletion>> ResolveAsync(
        ScriptEvolutionAcceptedReceipt receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        var decision = await _decisionReadPort.TryGetAsync(receipt.ProposalId, ct);
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
