using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web.Tools;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Narrow user-input source for routes that must not inherit the broader web tool set.
/// </summary>
public sealed class AskUserAgentToolSource : IAgentToolSource
{
    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new AskUserTool()]);
}
