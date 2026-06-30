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
    private readonly ILogger _logger;

    public ToolSetResponsesToolProvider(
        IReadOnlyList<IAgentToolSource> sources,
        ILogger logger)
    {
        _sources = sources ?? throw new ArgumentNullException(nameof(sources));
        _logger = logger ?? NullLogger.Instance;
    }

    public async ValueTask<IReadOnlyList<IAgentTool>> GetAdditiveToolsAsync(
        ResponsesToolProviderContext context,
        CancellationToken ct = default)
    {
        // Discovery runs on behalf of this request's caller, so the request's typed tool
        // context (NyxID access token, scope, channel) must be visible to context-aware
        // sources. IAgentToolSource.DiscoverToolsAsync has no context parameter, so publish
        // it through the AsyncLocal the tools already read at execution time.
        using var _ = AgentToolContextScope.Push(context.ToolContext);

        var tools = new List<IAgentTool>();
        foreach (var source in _sources)
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
                    "Responses route tool source discovery failed for source {SourceType}; continuing without that source.",
                    source.GetType().Name);
            }
        }

        return tools;
    }
}
