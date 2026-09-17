using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Ornn;

/// <summary>Opt-in Ornn skill publishing and update tools.</summary>
public sealed class OrnnAuthoringAgentToolSource : IAgentToolSource
{
    private readonly OrnnPublishSkillTool _publishTool;
    private readonly OrnnUpdateSkillTool _updateTool;

    public OrnnAuthoringAgentToolSource(
        OrnnPublishSkillTool publishTool,
        OrnnUpdateSkillTool updateTool)
    {
        _publishTool = publishTool ?? throw new ArgumentNullException(nameof(publishTool));
        _updateTool = updateTool ?? throw new ArgumentNullException(nameof(updateTool));
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([_publishTool, _updateTool]);
}
