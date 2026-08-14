using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web.Tools;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Exposes the effect-free typed proposal used by the conversation actor to
/// evaluate one numeric branch from already committed input facts.
/// </summary>
public sealed class ConditionEvaluateAgentToolSource : IAgentToolSource
{
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new ConditionEvaluateTool()]);
}
