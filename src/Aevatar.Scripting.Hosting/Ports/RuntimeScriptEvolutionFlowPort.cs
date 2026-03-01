using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Hosting.Ports;

public sealed class RuntimeScriptEvolutionFlowPort : IScriptEvolutionFlowPort
{
    private readonly IScriptPolicyGatePort _policyGatePort;
    private readonly IScriptValidationPipelinePort _validationPipelinePort;
    private readonly IScriptPromotionPort _promotionPort;

    public RuntimeScriptEvolutionFlowPort(
        IScriptPolicyGatePort policyGatePort,
        IScriptValidationPipelinePort validationPipelinePort,
        IScriptPromotionPort promotionPort)
    {
        _policyGatePort = policyGatePort ?? throw new ArgumentNullException(nameof(policyGatePort));
        _validationPipelinePort = validationPipelinePort ?? throw new ArgumentNullException(nameof(validationPipelinePort));
        _promotionPort = promotionPort ?? throw new ArgumentNullException(nameof(promotionPort));
    }

    public async Task<ScriptEvolutionFlowResult> ExecuteAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var policyDecision = await _policyGatePort.EvaluateAsync(proposal, ct);
        if (!policyDecision.IsAllowed)
        {
            var policyFailure = string.IsNullOrWhiteSpace(policyDecision.FailureReason)
                ? "Policy gate denied the proposal."
                : policyDecision.FailureReason;
            return ScriptEvolutionFlowResult.PolicyRejected(policyFailure);
        }

        var validation = await _validationPipelinePort.ValidateAsync(proposal, ct);
        if (!validation.IsSuccess)
            return ScriptEvolutionFlowResult.ValidationFailed(validation);

        try
        {
            var promotion = await _promotionPort.PromoteAsync(
                new ScriptPromotionRequest(
                    ProposalId: proposal.ProposalId ?? string.Empty,
                    ScriptId: proposal.ScriptId ?? string.Empty,
                    CandidateRevision: proposal.CandidateRevision ?? string.Empty,
                    CandidateSource: proposal.CandidateSource ?? string.Empty,
                    CandidateSourceHash: proposal.CandidateSourceHash ?? string.Empty,
                    DefinitionActorId: proposal.DefinitionActorId ?? string.Empty,
                    CatalogActorId: proposal.CatalogActorId ?? string.Empty),
                ct);

            return ScriptEvolutionFlowResult.Promoted(validation, promotion);
        }
        catch (Exception ex)
        {
            return ScriptEvolutionFlowResult.PromotionFailed(validation, ex.Message);
        }
    }

    public Task RollbackAsync(
        ScriptRollbackRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _promotionPort.RollbackAsync(request, ct);
    }
}
