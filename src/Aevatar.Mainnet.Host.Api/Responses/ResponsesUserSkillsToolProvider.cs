using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Ornn;
using Aevatar.AI.ToolProviders.Skills;
using Aevatar.GAgentService.Abstractions.Responses;
using Aevatar.GAgentService.Application.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Mainnet.Host.Api.Responses;

internal sealed class ResponsesUserSkillsToolProvider : IResponsesToolProvider
{
    private readonly IAgentToolSource _skillsSource;
    private readonly IAgentToolSource _ornnSource;
    private readonly ILogger _logger;

    public ResponsesUserSkillsToolProvider(
        SkillsAgentToolSource skillsSource,
        OrnnSearchAgentToolSource ornnSource,
        ILogger<ResponsesUserSkillsToolProvider>? logger = null)
        : this(
            (IAgentToolSource)(skillsSource ?? throw new ArgumentNullException(nameof(skillsSource))),
            (IAgentToolSource)(ornnSource ?? throw new ArgumentNullException(nameof(ornnSource))),
            logger)
    {
    }

    internal ResponsesUserSkillsToolProvider(
        IAgentToolSource skillsSource,
        IAgentToolSource ornnSource,
        ILogger<ResponsesUserSkillsToolProvider>? logger = null)
    {
        _skillsSource = skillsSource ?? throw new ArgumentNullException(nameof(skillsSource));
        _ornnSource = ornnSource ?? throw new ArgumentNullException(nameof(ornnSource));
        _logger = logger ?? NullLogger<ResponsesUserSkillsToolProvider>.Instance;
    }

    public async ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tools = new List<IAgentTool>();
        await AddDiscoveredToolsAsync(_skillsSource, tools, ct).ConfigureAwait(false);
        await AddDiscoveredToolsAsync(_ornnSource, tools, ct).ConfigureAwait(false);
        return tools;
    }

    private async ValueTask AddDiscoveredToolsAsync(
        IAgentToolSource source,
        List<IAgentTool> tools,
        CancellationToken ct)
    {
        try
        {
            tools.AddRange(await source.DiscoverToolsAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Responses user skill tool discovery failed for source {SourceType}; continuing without that source.",
                source.GetType().Name);
        }
    }
}
