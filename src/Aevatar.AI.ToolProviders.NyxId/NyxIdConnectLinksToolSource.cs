using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

public sealed class NyxIdConnectLinksToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdConnectLinksToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient client,
        ILogger<NyxIdConnectLinksToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<NyxIdConnectLinksToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.EffectiveTransportBaseUrl))
        {
            _logger.LogDebug("NyxID base URL not configured, skipping connect-link tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        return Task.FromResult<IReadOnlyList<IAgentTool>>([new NyxIdConnectLinksTool(_client)]);
    }
}
