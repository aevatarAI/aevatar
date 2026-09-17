using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Skills;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>
/// Narrow Ornn search source for routes that must not inherit skill publishing tools.
/// </summary>
public sealed class OrnnSearchAgentToolSource : IAgentToolSource
{
    private readonly OrnnSearchSkillsTool _tool;

    public OrnnSearchAgentToolSource(
        OrnnSkillClient client,
        IRemoteSkillAccessTokenResolver? remoteSkillAccessTokenResolver = null)
    {
        _tool = new OrnnSearchSkillsTool(client, remoteSkillAccessTokenResolver);
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([_tool]);
}
