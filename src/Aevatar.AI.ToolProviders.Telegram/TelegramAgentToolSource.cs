using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.AI.ToolProviders.Telegram.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.AI.ToolProviders.Telegram;

public sealed class TelegramAgentToolSource : IAgentToolSource
{
    private readonly TelegramToolOptions _options;
    private readonly NyxIdToolOptions _nyxOptions;
    private readonly ITelegramNyxClient _client;
    private readonly ILogger _logger;

    public TelegramAgentToolSource(
        TelegramToolOptions options,
        NyxIdToolOptions nyxOptions,
        ITelegramNyxClient client,
        ILogger<TelegramAgentToolSource>? logger = null)
    {
        _options = options;
        _nyxOptions = nyxOptions;
        _client = client;
        _logger = logger ?? NullLogger<TelegramAgentToolSource>.Instance;
    }

    public Task<IReadOnlyList<IAgentTool>> DiscoverToolsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_nyxOptions.BaseUrl))
        {
            _logger.LogDebug("NyxID base URL not configured, skipping typed Telegram tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        if (string.IsNullOrWhiteSpace(_options.ProviderSlug))
        {
            _logger.LogDebug("Telegram provider slug not configured, skipping typed Telegram tools");
            return Task.FromResult<IReadOnlyList<IAgentTool>>([]);
        }

        var tools = new List<IAgentTool>();
        if (_options.EnableMessageSend)
            tools.Add(new TelegramMessagesSendTool(_client));
        if (_options.EnableChatLookup)
            tools.Add(new TelegramChatsLookupTool(_client));

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
