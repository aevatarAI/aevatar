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
        if (_options.EnableMessageReply)
            tools.Add(new LarkMessagesReplyTool(_client));
        if (_options.EnableMessageReactionCreate)
            tools.Add(new LarkMessagesReactTool(_client));
        if (_options.EnableMessageReactionList)
            tools.Add(new LarkMessagesReactionsListTool(_client));
        if (_options.EnableMessageReactionDelete)
            tools.Add(new LarkMessagesReactionsDeleteTool(_client));
        if (_options.EnableMessageSearch)
            tools.Add(new LarkMessagesSearchTool(_client));
        if (_options.EnableMessageBatchGet)
            tools.Add(new LarkMessagesBatchGetTool(_client));
        if (_options.EnableChatLookup)
            tools.Add(new LarkChatsLookupTool(_client));
        if (_options.EnableSheetsAppendRows)
            tools.Add(new LarkSheetsAppendRowsTool(_client));
        if (_options.EnableApprovalsList)
            tools.Add(new LarkApprovalsListTool(_client));
        if (_options.EnableApprovalsAct)
            tools.Add(new LarkApprovalsActTool(_client));

        return Task.FromResult<IReadOnlyList<IAgentTool>>(tools);
    }
}
