using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Read-only authoritative Ornn skill inspection.</summary>
public sealed class OrnnReadAgentToolSource : IAgentToolSource
{
    private readonly OrnnReadSkillTool _tool;

    public OrnnReadAgentToolSource(OrnnReadSkillTool tool)
    {
        _tool = tool ?? throw new ArgumentNullException(nameof(tool));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([_tool]);
}
