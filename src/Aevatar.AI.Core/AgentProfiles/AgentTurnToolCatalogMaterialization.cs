using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.AgentProfiles;

public sealed class AgentProfileTurnAuthorityPreparation
{
    private readonly AgentProfileTurnAuthorityState _authority;
    private readonly IReadOnlyList<AgentProfileTurnDiagnostic> _diagnostics;
    private readonly AgentTurnToolCatalogProof? _shadowCandidateProof;

    private AgentProfileTurnAuthorityPreparation(
        AgentProfileTurnAuthorityState authority,
        IReadOnlyList<AgentProfileTurnDiagnostic> diagnostics,
        AgentTurnToolCatalogProof? shadowCandidateProof)
    {
        _authority = authority.Clone();
        _diagnostics = diagnostics.ToArray();
        _shadowCandidateProof = shadowCandidateProof;
    }

    public AgentProfileTurnAuthorityState Authority => _authority.Clone();

    public IReadOnlyList<AgentProfileTurnDiagnostic> Diagnostics => _diagnostics.ToArray();

    public AgentTurnToolCatalogProof? ShadowCandidateProof => _shadowCandidateProof;

    public static AgentProfileTurnAuthorityPreparation Create(
        AgentProfileTurnAuthorityState authority,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics = null,
        AgentTurnToolCatalogProof? shadowCandidateProof = null)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return new AgentProfileTurnAuthorityPreparation(authority, diagnostics ?? [], shadowCandidateProof);
    }
}

public sealed class AgentTurnToolCatalogMaterialization
{
    private readonly AgentProfileTurnAuthorityState _reconcileProposal;

    private AgentTurnToolCatalogMaterialization(
        AgentTurnToolCatalog catalog,
        AgentProfileTurnAuthorityState reconcileProposal)
    {
        Catalog = catalog;
        _reconcileProposal = reconcileProposal.Clone();
    }

    public AgentTurnToolCatalog Catalog { get; }

    public AgentProfileTurnAuthorityState ReconcileProposal => _reconcileProposal.Clone();

    public static AgentTurnToolCatalogMaterialization Create(
        AgentTurnToolCatalog catalog,
        AgentProfileTurnAuthorityState reconcileProposal)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(reconcileProposal);

        var ceiling = reconcileProposal.AuthorityCeilingToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!catalog.FinalAllowedToolNames.All(ceiling.Contains))
        {
            throw new InvalidOperationException(
                "The request-local catalog cannot grant tools outside the reconcile proposal ceiling.");
        }

        return new AgentTurnToolCatalogMaterialization(catalog, reconcileProposal);
    }
}
