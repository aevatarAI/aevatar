using Aevatar.AI.Abstractions;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.Core.AgentProfiles;

/// <summary>
/// Request-scoped planner shared by every profiled LLM ingress. Implementations may resolve
/// route tool sets and immutable Ornn skill bodies, but must not retain caller or turn authority
/// in process-local state.
/// </summary>
public interface IAgentProfileTurnToolCatalogPlanner
{
    Task<AgentProfileTurnAuthorityPreparation> PrepareAsync(
        AgentProfileSnapshot profile,
        string sessionId,
        string userMessage,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default);

    Task<AgentTurnToolCatalogMaterialization> MaterializeCommittedAsync(
        AgentProfileSnapshot profile,
        AgentProfileTurnAuthorityState committedAuthority,
        string? accessToken,
        IReadOnlyList<IAgentTool> registeredTools,
        AgentToolExecutionContext toolContext,
        CancellationToken ct = default);
}
