using Aevatar.AI.Abstractions.LLMProviders;
using Aevatar.AI.Abstractions.ToolProviders;

namespace Aevatar.AI.ToolProviders.Telegram.Tools;

public sealed class TelegramMessagesSendTool : AgentToolBase<TelegramMessagesSendTool.Parameters>
{
    private static readonly HashSet<string> AllowedParseModes =
    [
        "MarkdownV2",
        "HTML",
        "Markdown",
    ];

    private readonly ITelegramNyxClient _client;

    public TelegramMessagesSendTool(ITelegramNyxClient client)
    {
        _client = client;
    }

    public override string Name => "telegram_messages_send";

    public override string Description =>
        "Proactively send a Telegram message through Nyx-backed transport. " +
        "Use this for notifications or workflow side effects, not for replying to the current inbound relay turn.";

    public override ToolApprovalMode ApprovalMode => ToolApprovalMode.Auto;

    protected override async Task<string> ExecuteAsync(Parameters parameters, CancellationToken ct)
    {
        var token = AgentToolRequestContext.TryGet(LLMRequestMetadataKeys.NyxIdAccessToken);
        if (string.IsNullOrWhiteSpace(token))
            return TelegramProxyResponseParser.Serialize(new { success = false, error = "No NyxID access token available. User must be authenticated." });

        var chatId = parameters.ChatId?.Trim();
        if (string.IsNullOrWhiteSpace(chatId))
            return TelegramProxyResponseParser.Serialize(new { success = false, error = "chat_id is required" });

        var text = parameters.Text;
        if (string.IsNullOrWhiteSpace(text))
            return TelegramProxyResponseParser.Serialize(new { success = false, error = "text is required" });

        string? parseMode = null;
        if (!string.IsNullOrWhiteSpace(parameters.ParseMode))
        {
            var trimmed = parameters.ParseMode.Trim();
            if (!AllowedParseModes.Contains(trimmed))
            {
                return TelegramProxyResponseParser.Serialize(new
                {
                    success = false,
                    error = "parse_mode must be one of: MarkdownV2, HTML, Markdown",
                });
            }

            parseMode = trimmed;
        }

        var response = await _client.SendMessageAsync(
            token,
            new TelegramSendMessageRequest(
                ChatId: chatId,
                Text: text,
                ParseMode: parseMode,
                DisableNotification: parameters.DisableNotification,
                ReplyToMessageId: parameters.ReplyToMessageId),
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

        var result = TelegramProxyResponseParser.ParseSendSuccess(response);
        return TelegramProxyResponseParser.Serialize(new
        {
            success = true,
            chat_id = result.ChatId ?? chatId,
            message_id = result.MessageId,
            date = result.Date,
        });
    }

    public sealed class Parameters
    {
        public string? ChatId { get; set; }
        public string? Text { get; set; }
        public string? ParseMode { get; set; }
        public bool? DisableNotification { get; set; }
        public int? ReplyToMessageId { get; set; }
    }
}
