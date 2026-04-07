using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.ChannelRuntime.Adapters;

/// <summary>
/// Platform adapter for Telegram Bot API webhooks.
/// Parses Telegram Update payloads and sends replies via NyxID proxy.
/// </summary>
public sealed class TelegramPlatformAdapter : IPlatformAdapter
{
    private readonly ILogger<TelegramPlatformAdapter> _logger;

    public TelegramPlatformAdapter(ILogger<TelegramPlatformAdapter> logger) => _logger = logger;

    public string Platform => "telegram";

    /// <summary>
    /// Telegram does not use a challenge/response verification flow.
    /// Returns null to indicate this is not a verification request.
    /// </summary>
    public Task<IResult?> TryHandleVerificationAsync(
        HttpContext http, ChannelBotRegistrationEntry registration)
    {
        return Task.FromResult<IResult?>(null);
    }

    /// <summary>
    /// Parses a Telegram Update webhook payload into a normalized InboundMessage.
    /// Telegram Update format:
    /// {
    ///   "update_id": 123,
    ///   "message": {
    ///     "message_id": 456,
    ///     "date": 1234567890,
    ///     "chat": { "id": -987654321, "type": "private" },
    ///     "from": { "id": 12345, "is_bot": false, "first_name": "John", "username": "john" },
    ///     "text": "hello"
    ///   }
    /// }
    /// </summary>
    public async Task<InboundMessage?> ParseInboundAsync(
        HttpContext http, ChannelBotRegistrationEntry registration)
    {
        http.Request.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
        var root = doc.RootElement;

        // Telegram Update must have a "message" field for text messages
        if (!root.TryGetProperty("message", out var message))
        {
            _logger.LogDebug("Telegram update has no 'message' field (may be edited_message, callback_query, etc.), skipping");
            return null;
        }

        // Extract sender
        var senderId = string.Empty;
        var senderName = string.Empty;
        if (message.TryGetProperty("from", out var from))
        {
            senderId = from.TryGetProperty("id", out var fid) ? fid.GetRawText() : string.Empty;

            // Ignore messages from bots to prevent loops
            if (from.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean())
            {
                _logger.LogDebug("Ignoring message from bot sender");
                return null;
            }

            senderName = from.TryGetProperty("username", out var uname)
                ? uname.GetString() ?? string.Empty
                : from.TryGetProperty("first_name", out var fname)
                    ? fname.GetString() ?? string.Empty
                    : string.Empty;
        }

        // Extract chat
        if (!message.TryGetProperty("chat", out var chat))
        {
            _logger.LogWarning("Telegram message missing 'chat' field");
            return null;
        }

        var chatId = chat.TryGetProperty("id", out var cid) ? cid.GetRawText() : null;
        var chatType = chat.TryGetProperty("type", out var ctype) ? ctype.GetString() : null;

        if (chatId is null)
        {
            _logger.LogWarning("Telegram message missing chat.id");
            return null;
        }

        // Extract text
        var text = message.TryGetProperty("text", out var textProp) ? textProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.LogDebug("Telegram message has no text content, skipping");
            return null;
        }

        var messageId = message.TryGetProperty("message_id", out var mid)
            ? mid.GetRawText()
            : null;

        _logger.LogInformation("Telegram inbound: chat={ChatId}, sender={SenderId}, type={ChatType}",
            chatId, senderId, chatType);

        return new InboundMessage
        {
            Platform = Platform,
            ConversationId = chatId,
            SenderId = senderId,
            SenderName = senderName,
            Text = text,
            MessageId = messageId,
            ChatType = chatType,
        };
    }

    /// <summary>
    /// Send a reply via NyxID proxy → Telegram Bot API sendMessage.
    /// NyxID injects the bot token into the API path.
    /// </summary>
    public async Task SendReplyAsync(
        string replyText,
        InboundMessage inbound,
        ChannelBotRegistrationEntry registration,
        NyxIdApiClient nyxClient,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>
        {
            ["chat_id"] = long.Parse(inbound.ConversationId),
            ["text"] = replyText,
        };

        // Thread reply to original message if available
        if (!string.IsNullOrWhiteSpace(inbound.MessageId))
            payload["reply_to_message_id"] = long.Parse(inbound.MessageId);

        var body = JsonSerializer.Serialize(payload);

        var result = await nyxClient.ProxyRequestAsync(
            registration.NyxUserToken,
            registration.NyxProviderSlug,
            "bot/sendMessage",
            "POST",
            body,
            extraHeaders: null,
            ct);

        _logger.LogInformation(
            "Telegram outbound reply sent: chat={ChatId}, slug={Slug}, result_length={Length}",
            inbound.ConversationId, registration.NyxProviderSlug, result?.Length ?? 0);
    }
}
