using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Canonical Web runtime source. User interaction is exposed separately by
/// <see cref="AskUserAgentToolSource"/>.
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
        IReadOnlyList<IAgentTool> tools =
        [
            new WebSearchTool(_client, _options),
            new WebFetchTool(_client),
        ];

        _logger.LogInformation("Web tools registered ({Count} tools)", tools.Count);
        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
