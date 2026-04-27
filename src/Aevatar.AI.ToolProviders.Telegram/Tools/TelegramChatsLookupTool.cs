using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Telegram.Tools;

public sealed class TelegramChatsLookupTool : AgentToolBase<TelegramChatsLookupTool.Parameters>
{
    private readonly ITelegramNyxClient _client;

    public TelegramChatsLookupTool(ITelegramNyxClient client)
    {
        _client = client;
    }

    public override string Name => "telegram_chats_lookup";

    public override string Description =>
        "Look up Telegram chat metadata (id, type, title, username) by chat_id through Nyx-backed transport. " +
        "Read-only — useful for confirming chat identity or scoping before a send.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return TelegramProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var chatId = parameters.ChatId?.Trim();
        if (string.IsNullOrWhiteSpace(chatId))
            return TelegramProxyResponseParser.Serialize(new { success = false, error = "chat_id is required" });

        var response = await _client.GetChatAsync(
            token,
            new TelegramGetChatRequest(ChatId: chatId),
            ct);

        if (TelegramProxyResponseParser.TryParseError(response, out var error))
        {
            return TelegramProxyResponseParser.Serialize(new
            {
                success = false,
                error,
                chat_id = chatId,
            });
        }

        var info = TelegramProxyResponseParser.ParseChatSuccess(response);
        return TelegramProxyResponseParser.Serialize(new
        {
            success = true,
            chat_id = info.Id ?? chatId,
            type = info.Type,
            title = info.Title,
            username = info.Username,
        });
    }

    public sealed class Parameters
    {
        public string? ChatId { get; set; }
    }
}
