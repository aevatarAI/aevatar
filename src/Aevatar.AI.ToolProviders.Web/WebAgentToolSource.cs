using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Web tool source. Provides web search, fetch, and user interaction tools.
/// </summary>
public sealed class WebAgentToolSource : IAgentToolSource
{
    private readonly WebToolOptions _options;
    private readonly WebApiClient _client;
    private readonly ILogger _logger;

    public WebAgentToolSource(
        WebToolOptions options,
        WebApiClient client,
        ILogger<WebAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<WebAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<IAgentTool>();

        var hasSearchBackend = !string.IsNullOrWhiteSpace(_options.NyxIdSearchSlug) ||
                               !string.IsNullOrWhiteSpace(_options.SearchApiBaseUrl);
        if (hasSearchBackend)
            tools.Add(new WebSearchTool(_client, _options));

        // Web fetch and ask_user are always available
        tools.Add(new WebFetchTool(_client));
        tools.Add(new AskUserTool());

        _logger.LogInformation("Web tools registered ({Count} tools)", tools.Count);
        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
