using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Compilation;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptEvolutionFlowPort : IScriptEvolutionFlowPort
{
    private readonly IScriptPackageCompiler _compiler;
    private readonly IScriptLifecyclePort _lifecyclePort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public RuntimeScriptEvolutionFlowPort(
        IScriptPackageCompiler compiler,
        IScriptLifecyclePort lifecyclePort,
        IScriptingActorAddressResolver addressResolver)
    {
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
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

        var compilation = await _compiler.CompileAsync(
            new ScriptPackageCompilationRequest(
                proposal.ScriptId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                proposal.CandidateSource ?? string.Empty),
            ct);
        try
        {
            var validation = new ScriptEvolutionValidationReport(
                IsSuccess: compilation.IsSuccess,
                Diagnostics: compilation.Diagnostics ?? Array.Empty<string>());
            if (!validation.IsSuccess)
                return ScriptEvolutionFlowResult.ValidationFailed(validation);

            var scriptId = proposal.ScriptId ?? string.Empty;
            var candidateRevision = proposal.CandidateRevision ?? string.Empty;
            var catalogActorId = _addressResolver.GetCatalogActorId();
            ScriptCatalogEntrySnapshot? catalogBefore;
            var catalogBaselineSource = "query";
            try
            {
                catalogBefore = await _lifecyclePort.GetCatalogEntryAsync(catalogActorId, scriptId, ct);
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(proposal.BaseRevision))
                {
                    return ScriptEvolutionFlowResult.PromotionFailed(
                        validation,
                        "Failed to load catalog baseline before promotion and no base revision fallback is available. reason=" +
                        ex.Message);
                }

                catalogBefore = BuildFallbackCatalogBaseline(proposal);
                catalogBaselineSource = "fallback_base_revision_after_query_failure";
            }

            if (catalogBefore == null && !string.IsNullOrWhiteSpace(proposal.BaseRevision))
            {
                catalogBefore = BuildFallbackCatalogBaseline(proposal);
                catalogBaselineSource = "fallback_base_revision_after_null_query";
            }

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
                var compensation = await TryCompensateCatalogRevisionAsync(
                    catalogActorId,
                    proposal,
                    catalogBefore,
                    ct);
                var partial = new ScriptPromotionResult(
                    DefinitionActorId: definitionActorId,
                    CatalogActorId: catalogActorId,
                    PromotedRevision: candidateRevision);
                return ScriptEvolutionFlowResult.PromotionFailed(
                    validation,
                    "Promotion failed after definition upsert. definition_actor_id=" +
                    definitionActorId +
                    " candidate_revision=" + candidateRevision +
                    " catalog_baseline_source=" + catalogBaselineSource +
                    " reason=" + ex.Message +
                    " compensation=" + compensation,
                    partial);
            }
        }
        finally
        {
            await DisposeCompiledDefinitionAsync(compilation.CompiledDefinition);
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

    private static ScriptCatalogEntrySnapshot BuildFallbackCatalogBaseline(ScriptEvolutionProposal proposal)
    {
        var scriptId = proposal.ScriptId ?? string.Empty;
        var baseRevision = proposal.BaseRevision ?? string.Empty;
        return new ScriptCatalogEntrySnapshot(
            ScriptId: scriptId,
            ActiveRevision: baseRevision,
            ActiveDefinitionActorId: string.Empty,
            ActiveSourceHash: string.Empty,
            PreviousRevision: string.Empty,
            RevisionHistory: [baseRevision],
            LastProposalId: proposal.ProposalId ?? string.Empty);
    }

    private async Task<string> TryCompensateCatalogRevisionAsync(
        string catalogActorId,
        ScriptEvolutionProposal proposal,
        ScriptCatalogEntrySnapshot? catalogBefore,
        CancellationToken ct)
    {
        var scriptId = proposal.ScriptId ?? string.Empty;
        if (catalogBefore == null || string.IsNullOrWhiteSpace(catalogBefore.ActiveRevision))
            return "not_required";

        try
        {
            await _lifecyclePort.RollbackCatalogRevisionAsync(
                catalogActorId,
                scriptId,
                catalogBefore.ActiveRevision,
                "compensate promotion failure",
                proposal.ProposalId ?? string.Empty,
                proposal.CandidateRevision ?? string.Empty,
                ct);
            return "rollback_to_previous_active_revision_success";
        }
        catch (Exception ex)
        {
            return "rollback_failed:" + ex.Message;
        }
    }

    private static async Task DisposeCompiledDefinitionAsync(IScriptPackageDefinition? definition)
    {
        if (definition is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
            return;
        }

        if (definition is IDisposable disposable)
            disposable.Dispose();
    }
}
