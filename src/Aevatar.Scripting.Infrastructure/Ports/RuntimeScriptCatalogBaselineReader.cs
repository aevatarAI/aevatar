using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptCatalogBaselineReader : IScriptCatalogBaselineReader
{
    private readonly IScriptCatalogQueryPort _catalogQueryPort;
    private readonly IScriptingActorAddressResolver _addressResolver;

    public RuntimeScriptCatalogBaselineReader(
        IScriptCatalogQueryPort catalogQueryPort,
        IScriptingActorAddressResolver addressResolver)
    {
        _catalogQueryPort = catalogQueryPort ?? throw new ArgumentNullException(nameof(catalogQueryPort));
        _addressResolver = addressResolver ?? throw new ArgumentNullException(nameof(addressResolver));
    }

    public async Task<ScriptCatalogBaselineResolution> ReadAsync(
        ScriptEvolutionProposal proposal,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proposal);

        var scriptId = proposal.ScriptId ?? string.Empty;
        var catalogActorId = _addressResolver.GetCatalogActorId();
        ScriptCatalogEntrySnapshot? catalogBefore;
        var catalogBaselineSource = "query";

        try
        {
            catalogBefore = await _catalogQueryPort.GetCatalogEntryAsync(catalogActorId, scriptId, ct);
        }
        catch (Exception ex)
        {
            if (string.IsNullOrWhiteSpace(proposal.BaseRevision))
            {
                return new ScriptCatalogBaselineResolution(
                    CatalogActorId: catalogActorId,
                    Baseline: null,
                    BaselineSource: "query_failed_without_fallback",
                    FailureReason: "Failed to load catalog baseline before promotion and no base revision fallback is available. reason=" +
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
