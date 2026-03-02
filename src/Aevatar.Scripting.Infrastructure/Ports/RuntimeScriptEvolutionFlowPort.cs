using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionFlowPort : IScriptEvolutionFlowPort
{
    private readonly IScriptPackageCompiler _compiler;
    private readonly IScriptDefinitionLifecyclePort _definitionLifecyclePort;
    private readonly IScriptCatalogPort _catalogPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public RuntimeScriptEvolutionFlowPort(
        IScriptPackageCompiler compiler,
        IScriptDefinitionLifecyclePort definitionLifecyclePort,
        IScriptCatalogPort catalogPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _definitionLifecyclePort = definitionLifecyclePort ?? throw new ArgumentNullException(nameof(definitionLifecyclePort));
        _catalogPort = catalogPort ?? throw new ArgumentNullException(nameof(catalogPort));
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

        var compilation = await _compiler.CompileAsync(
            new ScriptPackageCompilationRequest(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                proposal.CandidateSource ?? string.Empty),
            ct);
        var validation = new ScriptEvolutionValidationReport(
            IsSuccess: compilation.IsSuccess,
            Diagnostics: compilation.Diagnostics ?? Array.Empty<string>());
        if (!validation.IsSuccess)
            return ScriptEvolutionFlowResult.ValidationFailed(validation);

        try
        {
            var definitionActorId = await _definitionLifecyclePort.UpsertAsync(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                proposal.CandidateSource ?? string.Empty,
                proposal.CandidateSourceHash ?? string.Empty,
                null,
                ct);
            var catalogActorId = _addressResolver.GetCatalogActorId();
            await _catalogPort.PromoteAsync(
                catalogActorId,
                proposal.ScriptId ?? string.Empty,
                proposal.BaseRevision ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                definitionActorId,
                proposal.CandidateSourceHash ?? string.Empty,
                proposal.ProposalId ?? string.Empty,
                ct);

            var promotion = new ScriptPromotionResult(
                DefinitionActorId: definitionActorId,
                CatalogActorId: catalogActorId,
                PromotedRevision: proposal.CandidateRevision ?? string.Empty);

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
        var catalogActorId = string.IsNullOrWhiteSpace(request.CatalogActorId)
            ? _addressResolver.GetCatalogActorId()
            : request.CatalogActorId;

        return _catalogPort.RollbackAsync(
            catalogActorId,
            request.ScriptId,
            request.TargetRevision,
            request.Reason,
            request.ProposalId,
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
