using Aevatar.CQRS.Core.Abstractions.Interactions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Application;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionInteractionService
    : IScriptEvolutionProposalPort
{
    private readonly ICommandInteractionService<ScriptEvolutionProposal, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion> _interactionService;

    public RuntimeScriptEvolutionInteractionService(
        ICommandInteractionService<ScriptEvolutionProposal, ScriptEvolutionAcceptedReceipt, ScriptEvolutionStartError, ScriptEvolutionSessionCompletedEvent, ScriptEvolutionInteractionCompletion> interactionService)
    {
        _interactionService = interactionService ?? throw new ArgumentNullException(nameof(interactionService));
    }

    public async Task<ScriptPromotionDecision> ProposeAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var normalizedProposalId = string.IsNullOrWhiteSpace(proposal.ProposalId)
            ? Guid.NewGuid().ToString("N")
            : proposal.ProposalId;
        var normalizedProposal = proposal with { ProposalId = normalizedProposalId };

        var result = await _interactionService.ExecuteAsync(
            normalizedProposal,
            static (_, _) => ValueTask.CompletedTask,
            ct: ct);
        if (!result.Succeeded)
            throw MapStartError(result.Error);

        var finalize = result.FinalizeResult
            ?? throw new InvalidOperationException("Script evolution interaction did not produce a finalize result.");
        if (!finalize.Completed)
        {
            throw new TimeoutException(
                $"Timeout waiting for script evolution session completion. proposal_id={normalizedProposalId}");
        }

        return finalize.Completion.ToPromotionDecision(normalizedProposal);
    }

    private static Exception MapStartError(ScriptEvolutionStartError error) =>
        error switch
        {
            ScriptEvolutionStartError.ProjectionDisabled =>
                new InvalidOperationException("Script evolution projection is disabled."),
            _ => new InvalidOperationException($"Script evolution could not start. error={error}."),
        };
}
