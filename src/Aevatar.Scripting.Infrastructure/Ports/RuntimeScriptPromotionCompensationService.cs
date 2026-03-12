using Aevatar.Scripting.Abstractions.Definitions;
using Aevatar.Scripting.Core.Ports;

namespace Aevatar.Scripting.Infrastructure.Ports;

public sealed class RuntimeScriptPromotionCompensationService : IScriptPromotionCompensationService
{
    private readonly IScriptCatalogCommandPort _catalogCommandPort;

    public RuntimeScriptPromotionCompensationService(IScriptCatalogCommandPort catalogCommandPort)
    {
        _catalogCommandPort = catalogCommandPort ?? throw new ArgumentNullException(nameof(catalogCommandPort));
    }

    public async Task<string> TryCompensateAsync(
        string catalogActorId,
        ScriptEvolutionProposal proposal,
        ScriptCatalogEntrySnapshot? catalogBefore,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogActorId);
        ArgumentNullException.ThrowIfNull(proposal);

        var scriptId = proposal.ScriptId ?? string.Empty;
        if (catalogBefore == null || string.IsNullOrWhiteSpace(catalogBefore.ActiveRevision))
            return "not_required";

        try
        {
            await _catalogCommandPort.RollbackCatalogRevisionAsync(
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
}
