using System.Text;
using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.Prompting;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.AgentProfiles;

/// <summary>Single final-catalog factory used after every profile planning path.</summary>
public static class AgentTurnToolCatalogFactory
{
    public static AgentTurnToolCatalog CreateForProfile(
        AgentProfileSnapshot profile,
        IEnumerable<string> finalNames,
        string? selectedIntentId,
        string? candidateIntentId,
        SelectedSkillPromptLayer? selectedSkillPromptLayer,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics,
        IEnumerable<IAgentTool> exactTools,
        bool hasUnresolvedConnectedServiceSelectors = false,
        AgentProfileRequiredToolInvocation? requiredToolInvocation = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var profileText = new StringBuilder()
            .Append("Agent profile: ").Append(profile.ProfileId)
            .Append("\nProfile version: ").Append(profile.ProfileVersion)
            .Append("\nPolicy revision: ").Append(profile.PolicyRevision);
        if (!string.IsNullOrWhiteSpace(candidateIntentId))
            profileText.Append("\nCandidate intent: ").Append(candidateIntentId);
        if (!string.IsNullOrWhiteSpace(selectedIntentId))
            profileText.Append("\nSelected intent: ").Append(selectedIntentId);
        if (!string.IsNullOrWhiteSpace(profile.Instructions))
            profileText.Append("\nProfile instructions:\n").Append(profile.Instructions.Trim());

        var profileLayer = new ProfileRoutingPromptLayer(
            profileText.ToString(),
            new ProfileRoutingPromptProvenance(
                $"agent-profile:{profile.ProfileId}@{profile.ProfileVersion}"),
            new PromptLayerBounds(8 * 1024, 2 * 1024));
        return new AgentTurnToolCatalog(
            finalNames,
            profileLayer,
            selectedSkillPromptLayer,
            selectedIntentId,
            candidateIntentId,
            diagnostics,
            exactTools,
            hasUnresolvedConnectedServiceSelectors,
            requiredToolInvocation,
            ResolveProfileBudget(profile));
    }

    public static AgentTurnToolCatalog RestrictedEmpty(
        AgentTurnToolCatalogBudget? budget = null,
        IReadOnlyList<AgentProfileTurnDiagnostic>? diagnostics = null) =>
        new(
            [],
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: null,
            candidateIntentId: null,
            diagnostics,
            budget: budget ?? AgentTurnToolCatalogBudget.Ordinary);

    public static AgentTurnToolCatalogBudget ResolveProfileBudget(AgentProfileSnapshot profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var maximumToolCount = profile.HasMaxOwnedToolCount
            ? profile.MaxOwnedToolCount
            : AgentTurnToolCatalogBudget.Ordinary.MaximumToolCount;
        var maximumSchemaBytes = profile.HasMaxSchemaBytes
            ? profile.MaxSchemaBytes
            : AgentTurnToolCatalogBudget.Ordinary.MaximumSchemaBytes;
        if (maximumToolCount < 0 ||
            maximumToolCount > AgentTurnToolCatalogBudget.Ordinary.MaximumToolCount ||
            maximumSchemaBytes < 0 ||
            maximumSchemaBytes > AgentTurnToolCatalogBudget.Ordinary.MaximumSchemaBytes ||
            maximumToolCount > 0 && maximumSchemaBytes == 0)
        {
            throw new AgentTurnToolCatalogException(new AgentTurnToolCatalogFailure(
                AgentTurnToolCatalogFailureCode.CatalogOverBudget,
                "The sealed profile catalog budget exceeds the ordinary route ceiling."));
        }

        return new AgentTurnToolCatalogBudget(
            maximumToolCount,
            maximumSchemaBytes,
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedReadToolCount,
            AgentTurnToolCatalogBudget.ConnectedOperations.MaximumConnectedWriteToolCount);
    }
}
