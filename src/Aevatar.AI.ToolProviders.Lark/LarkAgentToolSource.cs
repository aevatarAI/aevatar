using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.Lark.Tools;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Lark;

public sealed class LarkAgentToolSource : IAgentToolSource
{
    private readonly LarkToolOptions _options;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly ILarkNyxClient _client;
    private readonly ILogger _logger;

    public LarkAgentToolSource(
        LarkToolOptions options,
        NyxIdToolOptions nyxOptions,
        ILarkNyxClient client,
        ILogger<LarkAgentToolSource>? logger = null)
    {
        _options = options;
        _nyxOptions = nyxOptions;
        _client = client;
        _logger = logger ?? NullLogger<LarkAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_nyxOptions.BaseUrl))
        {
            _logger.LogDebug("NyxID base URL not configured, skipping typed Lark tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        if (string.IsNullOrWhiteSpace(_options.ProviderSlug))
        {
            _logger.LogDebug("Lark provider slug not configured, skipping typed Lark tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        var tools = new List<IAgentTool>();
        if (_options.EnableMessageSend)
            tools.Add(new LarkMessagesSendTool(_client));
        if (_options.EnableChatLookup)
            tools.Add(new LarkChatsLookupTool(_client));

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
