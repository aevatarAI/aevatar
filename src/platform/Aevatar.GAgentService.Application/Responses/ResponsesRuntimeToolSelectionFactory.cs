using Aevatar.GAgentService.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.GAgentService.Application.Responses;

/// <summary>
/// Single builder for the durable <see cref="LlmSessionRuntimeToolSelection"/> written into an
/// <c>LlmRunRequested</c> command. Shared by all three OpenAI-compatible ingress facades
/// (responses / messages / chat-completions) so the persisted tool-selection shape — including the
/// route <c>tool_set_name</c> the off-grain run executor re-resolves against — cannot drift between
/// ingresses (was previously copy-pasted three times).
/// </summary>
internal static class ResponsesRuntimeToolSelectionFactory
{
    public static LlmSessionRuntimeToolSelection Create(
        ResponsesToolClassification classification,
        ResponsesToolChoiceHintPlan toolChoiceHintPlan,
        string? routeToolSetName,
        Aevatar.AI.Abstractions.AgentProfileSnapshot? profileSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(classification);
        var catalog = classification.OwnedCatalog;
        if (catalog is null && classification.OwnedToolNames.Count > 0)
        {
            throw new InvalidOperationException(
                "A Responses run cannot persist owned tool names without the frozen exact catalog.");
        }

        catalog ??= Aevatar.AI.Core.AgentProfiles.AgentTurnToolCatalogFactory.RestrictedEmpty();
        var exactNames = catalog.ExactTools.Values
            .Select(static tool => tool.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!exactNames.SetEquals(classification.OwnedToolNames))
        {
            throw new InvalidOperationException(
                "Responses owned tool names do not match the frozen exact catalog.");
        }

        var selection = new LlmSessionRuntimeToolSelection
        {
            SubstitutedToolNames = { classification.SubstitutedToolNames },
            AdditiveToolNames = { classification.AdditiveToolNames },
            OwnedToolNames = { classification.OwnedToolNames },
            OwnedCatalogProof = catalog.Proof.ToPayload(),
            ToolCatalogPolicyVersion = ResponsesOwnedToolCatalogPlanner.PolicyVersion,
        };

        if (profileSnapshot is not null)
            selection.ProfileSnapshot = profileSnapshot.Clone();

        // Route tool set the facade classified against. The off-grain LlmRunCore re-resolves this
        // name from IToolSetRegistry to re-materialize the same sources; empty = always-on DI
        // providers only. Without it the run drops every route-tool-set tool (nyxid_*, invoke_*, ...).
        if (!string.IsNullOrWhiteSpace(routeToolSetName))
            selection.ToolSetName = routeToolSetName;

        if (!toolChoiceHintPlan.IsEmpty)
        {
            selection.ToolChoiceHintName = toolChoiceHintPlan.ToolName;
            selection.ToolChoiceHintArgumentsJson = toolChoiceHintPlan.PrefilledArgumentsJson();
            selection.ToolChoiceHintArguments = toolChoiceHintPlan.PrefilledArgumentsStruct();
        }

        selection.ForwardedTools.AddRange(classification.ForwardedTools.Select(static tool =>
            new LlmSessionRuntimeToolDeclaration
            {
                ToolName = tool.Name,
                Description = tool.Description,
                ParametersJson = tool.ParametersJson,
                Parameters = ResponsesProtoPayloads.ParseStruct(tool.ParametersJson),
                SchemaHash = tool.SchemaHash,
            }));

        return selection;
    }
}
