using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionFlowPort : IScriptEvolutionFlowPort
{
    private readonly IScriptLifecyclePort _lifecyclePort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public RuntimeScriptEvolutionFlowPort(
        IScriptLifecyclePort lifecyclePort,
        IScriptingActorAddressResolver addressResolver)
    {
        _lifecyclePort = lifecyclePort ?? throw new ArgumentNullException(nameof(lifecyclePort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<ScriptEvolutionFlowResult> ExecuteAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var policyFailure = EvaluatePolicyFailure(proposal);
        if (!string.IsNullOrWhiteSpace(policyFailure))
            return ScriptEvolutionFlowResult.PolicyRejected(policyFailure);

        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: true,
            Diagnostics: Array.Empty<string>());
        var scriptId = proposal.ScriptId ?? string.Empty;
        var candidateRevision = proposal.CandidateRevision ?? string.Empty;
        var catalogActorId = _addressResolver.GetCatalogActorId();

        string definitionActorId;
        try
        {
            definitionActorId = await _lifecyclePort.UpsertDefinitionAsync(
                scriptId,
                candidateRevision,
                proposal.CandidateSource ?? string.Empty,
                proposal.CandidateSourceHash ?? string.Empty,
                null,
                ct);
        }
        catch (ScriptDefinitionMutationRejectedException ex)
        {
            var diagnostics = ex.Diagnostics.Count > 0
                ? ex.Diagnostics
                : [ex.Message];
            return ScriptEvolutionFlowResult.ValidationFailed(
                new ScriptEvolutionValidationReport(false, diagnostics));
        }
        catch (Exception ex)
        {
            return ScriptEvolutionFlowResult.PromotionFailed(
                validation,
                "Failed to upsert candidate definition before promotion. reason=" + ex.Message);
        }

        try
        {
            await _lifecyclePort.PromoteCatalogRevisionAsync(
                catalogActorId,
                scriptId,
                proposal.BaseRevision ?? string.Empty,
                candidateRevision,
                definitionActorId,
                proposal.CandidateSourceHash ?? string.Empty,
                proposal.ProposalId ?? string.Empty,
                ct);

            var promotion = new ScriptPromotionResult(
                DefinitionActorId: definitionActorId,
                CatalogActorId: catalogActorId,
                PromotedRevision: candidateRevision);

            return ScriptEvolutionFlowResult.Promoted(validation, promotion);
        }
        catch (Exception ex)
        {
            var partial = new ScriptPromotionResult(
                DefinitionActorId: definitionActorId,
                CatalogActorId: catalogActorId,
                PromotedRevision: candidateRevision);
            return ScriptEvolutionFlowResult.PromotionFailed(
                validation,
                "Promotion failed after definition upsert. definition_actor_id=" +
                definitionActorId +
                " candidate_revision=" + candidateRevision +
                " reason=" + ex.Message,
                partial);
        }
    }

    public Task RollbackAsync(
        ScriptRollbackRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalogActorId = string.IsNullOrWhiteSpace(request.CatalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : request.CatalogActorId;

        return _lifecyclePort.RollbackCatalogRevisionAsync(
            catalogActorId,
            request.ScriptId,
            request.TargetRevision,
            request.Reason,
            request.ProposalId,
            request.ExpectedCurrentRevision,
            ct);
    }

    private static string EvaluatePolicyFailure(ScriptEvolutionProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.ScriptId))
            return "ScriptId is required.";
        if (string.IsNullOrWhiteSpace(proposal.CandidateRevision))
            return "CandidateRevision is required.";
        if (string.IsNullOrWhiteSpace(proposal.CandidateSource))
            return "CandidateSource is required.";
        if (string.IsNullOrWhiteSpace(proposal.CandidateSourceHash))
            return "CandidateSourceHash is required.";
        if (!string.IsNullOrWhiteSpace(proposal.BaseRevision) &&
            string.Equals(proposal.BaseRevision, proposal.CandidateRevision, StringComparison.Ordinal))
        {
            return "CandidateRevision must differ from BaseRevision.";
        }

        return string.Empty;
    }
}
