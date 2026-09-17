using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Web.Tools;

namespace Aevatar.AI.ToolProviders.Web;

/// <summary>
/// Narrow web-search source for routes that must not inherit fetch or user-input tools.
/// </summary>
public sealed class WebSearchAgentToolSource : IAgentToolSource
{
    private readonly WebToolOptions _options;
    private readonly WebApiClient _client;

    public WebSearchAgentToolSource(WebToolOptions options, WebApiClient client)
    {
        _options = options;
        _client = client;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<IAgentTool>>([new WebSearchTool(_client, _options)]);
}
