using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogBaselineReader : IScriptCatalogBaselineReader
{
    private readonly IScriptingActorAddressResolver _addressResolver;

    public RuntimeScriptCatalogBaselineReader(
        IScriptingActorAddressResolver addressResolver)
    {
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<ScriptCatalogBaselineResolution> ReadAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var scriptId = proposal.ScriptId ?? string.Empty;
        var catalogActorId = _addressResolver.GetCatalogActorId(proposal.ScopeId);
        ct.ThrowIfCancellationRequested();

        var catalogBefore = string.IsNullOrWhiteSpace(proposal.BaseRevision)
            ? null
            : BuildFallbackCatalogBaseline(proposal);
        var catalogBaselineSource = catalogBefore == null
            ? "no_baseline"
            : "proposal_base_revision";

        return new ScriptCatalogBaselineResolution(
            CatalogActorId: catalogActorId,
            Baseline: catalogBefore,
            BaselineSource: catalogBaselineSource,
            FailureReason: string.Empty);
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
}
