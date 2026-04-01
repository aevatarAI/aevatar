using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.NyxId;

/// <summary>
/// NyxID tool source. Provides tools for managing services, credentials,
/// nodes, approvals, and making proxied requests through NyxID.
/// </summary>
public sealed class NyxIdAgentToolSource : IAgentToolSource
{
    private readonly NyxIdToolOptions _options;
    private readonly NyxIdApiClient _client;
    private readonly ILogger _logger;

    public NyxIdAgentToolSource(
        NyxIdToolOptions options,
        NyxIdApiClient client,
        ILogger<NyxIdAgentToolSource>? logger = null)
    {
        _options = options;
        _client = client;
        _logger = logger ?? NullLogger<NyxIdAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _logger.LogDebug("NyxID base URL not configured, skipping NyxID tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        IReadOnlyList<IAgentTool> tools =
        [
            new NyxIdAccountTool(_client),
            new NyxIdStatusTool(_client),
            new NyxIdProfileTool(_client),
            new NyxIdMfaTool(_client),
            new NyxIdSessionsTool(_client),
            new NyxIdCatalogTool(_client),
            new NyxIdServicesTool(_client),
            new NyxIdProxyTool(_client, _logger),
            new NyxIdApiKeysTool(_client),
            new NyxIdNodesTool(_client),
            new NyxIdApprovalsTool(_client),
            new NyxIdEndpointsTool(_client),
            new NyxIdExternalKeysTool(_client),
            new NyxIdNotificationsTool(_client),
            new NyxIdLlmStatusTool(_client),
            new NyxIdProvidersTool(_client),
        ];

        _logger.LogInformation(
            "NyxID tools registered ({Count} tools, base URL: {BaseUrl})",
            tools.Count, _options.BaseUrl);

        return Task.FromResult(tools);
    }
}
