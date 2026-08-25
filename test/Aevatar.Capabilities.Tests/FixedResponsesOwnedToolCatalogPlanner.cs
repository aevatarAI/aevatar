using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.Core.AgentProfiles;
using Aevatar.ChatRouting.Abstractions;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Capabilities.Tests;

internal sealed class FixedResponsesOwnedToolCatalogPlanner : IResponsesOwnedToolCatalogPlanner
{
    private readonly string _toolSetName;
    private readonly IReadOnlyList<IAgentTool> _tools;
    private readonly AgentProfileSnapshot _profile;

    public FixedResponsesOwnedToolCatalogPlanner(
        string toolSetName,
        params IAgentTool[] tools)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolSetName);
        ArgumentNullException.ThrowIfNull(tools);

        _toolSetName = toolSetName.Trim();
        _tools = tools.ToArray();
        _profile = AgentProfileSnapshotCodec.Seal(new AgentProfileSnapshot
        {
            ProfileId = "profile-capabilities-test",
            ProfileVersion = "1.0.0",
            AgentKind = "workspace.chat",
            PolicyRevision = "policy-capabilities-test",
            RouteToolSetRef = _toolSetName,
            PublishedRevision = 1,
            MaxOwnedToolCount = AgentTurnToolCatalogBudget.Ordinary.MaximumToolCount,
            MaxSchemaBytes = AgentTurnToolCatalogBudget.Ordinary.MaximumSchemaBytes,
        });
    }

    public Task<ResponsesOwnedToolCatalogPlan> PlanAsync(
        ChatRouteAction? routeAction,
        string scopeId,
        string turnIdentity,
        string userMessage,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default)
    {
        var catalog = new AgentTurnToolCatalog(
            _tools.Select(static tool => tool.Name),
            profilePromptLayer: null,
            selectedSkillPromptLayer: null,
            selectedIntentId: "capabilities-test",
            candidateIntentId: "capabilities-test",
            diagnostics: null,
            exactTools: _tools);
        return Task.FromResult(new ResponsesOwnedToolCatalogPlan(
            catalog,
            _profile,
            _toolSetName,
            null));
    }
}
