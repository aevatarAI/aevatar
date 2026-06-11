using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class ResponsesUserSkillsToolProvider : IResponsesToolProvider
{
    private readonly SkillsAgentToolSource _skillsSource;
    private readonly OrnnAgentToolSource _ornnSource;

    public ResponsesUserSkillsToolProvider(
        SkillsAgentToolSource skillsSource,
        OrnnAgentToolSource ornnSource)
    {
        _skillsSource = skillsSource ?? throw new ArgumentNullException(nameof(skillsSource));
        _ornnSource = ornnSource ?? throw new ArgumentNullException(nameof(ornnSource));
    }

    public async ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tools = new List<IAgentTool>();
        tools.AddRange(await _skillsSource.DiscoverToolsAsync(ct).ConfigureAwait(false));
        tools.AddRange(await _ornnSource.DiscoverToolsAsync(ct).ConfigureAwait(false));
        return tools;
    }
}
