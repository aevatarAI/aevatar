using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Workflow-authoring surface that exposes private skill publication without update tools.</summary>
public sealed class OrnnPublishAgentToolSource : IAgentToolSource
{
    private readonly OrnnPublishSkillTool _publishTool;

    public OrnnPublishAgentToolSource(OrnnPublishSkillTool publishTool)
    {
        _publishTool = publishTool ?? throw new ArgumentNullException(nameof(publishTool));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([_publishTool]);
}
