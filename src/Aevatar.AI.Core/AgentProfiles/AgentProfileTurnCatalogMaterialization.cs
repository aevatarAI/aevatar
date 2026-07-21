using Aevatar.AI.Abstractions;

namespace Aevatar.AI.Core.AgentProfiles;

public sealed class AgentProfileTurnAuthorityPreparation
{
    private readonly AgentProfileTurnAuthorityState _authority;

    private AgentProfileTurnAuthorityPreparation(AgentProfileTurnAuthorityState authority)
    {
        _authority = authority.Clone();
    }

    public AgentProfileTurnAuthorityState Authority => _authority.Clone();

    public static AgentProfileTurnAuthorityPreparation Create(AgentProfileTurnAuthorityState authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return new AgentProfileTurnAuthorityPreparation(authority);
    }
}

public sealed class AgentProfileTurnCatalogMaterialization
{
    private readonly AgentProfileTurnAuthorityState _reconcileProposal;

    private AgentProfileTurnCatalogMaterialization(
        AgentProfileTurnCatalog catalog,
        AgentProfileTurnAuthorityState reconcileProposal)
    {
        Catalog = catalog;
        _reconcileProposal = reconcileProposal.Clone();
    }

    public AgentProfileTurnCatalog Catalog { get; }

    public AgentProfileTurnAuthorityState ReconcileProposal => _reconcileProposal.Clone();

    public static AgentProfileTurnCatalogMaterialization Create(
        AgentProfileTurnCatalog catalog,
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

        return new AgentProfileTurnCatalogMaterialization(catalog, reconcileProposal);
    }
}
