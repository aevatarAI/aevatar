using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgentService.Abstractions.Responses;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Application.Responses;

/// <summary>
/// Adapts a resolved tool set's <see cref="IAgentToolSource"/> list to an
/// <see cref="IResponsesToolProvider"/> whose tools are all additive. Shared by the facade
/// (<see cref="ResponsesDirectToolPlanService"/>) when classifying a request and the off-grain
/// run executor (<c>LlmRunCore</c>) when re-resolving the same route tool set, so both
/// materialize identical sources from the same registry.
/// </summary>
internal sealed class ToolSetResponsesToolProvider : IResponsesToolProvider
{
    private readonly IReadOnlyList<IAgentToolSource> _sources;
    private readonly IAgentToolDiscoveryService _toolDiscoveryService;
    private readonly ILogger _logger;

    public ToolSetResponsesToolProvider(
        IReadOnlyList<IAgentToolSource> sources,
        ILogger logger,
        IAgentToolDiscoveryService? toolDiscoveryService = null)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _toolDiscoveryService = toolDiscoveryService ?? AgentToolDiscoveryService.Instance;
        _logger = logger ?? NullLogger.Instance;
    }

    public async ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default)
    {
        var result = await _toolDiscoveryService
            .DiscoverAsync(_sources, context.ToolContext, ct)
            .ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _logger.LogError(
                "Responses route tool discovery failed closed. code={FailureCode} tool={ToolName} source={SourceType} conflictingSource={ConflictingSourceType}",
                result.Failure!.Code,
                result.Failure.ToolName,
                result.Failure.SourceType,
                result.Failure.ConflictingSourceType);
            throw new AgentToolDiscoveryException(result.Failure);
        }

        return result.Tools;
    }
}
