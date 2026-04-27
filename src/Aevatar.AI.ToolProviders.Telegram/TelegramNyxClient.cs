using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aevatar.AI.ToolProviders.NyxId;

namespace Aevatar.AI.ToolProviders.Telegram;

public sealed class TelegramNyxClient : ITelegramNyxClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TelegramToolOptions _options;
    private readonly NyxIdApiClient _nyxClient;

    public TelegramNyxClient(TelegramToolOptions options, NyxIdApiClient nyxClient)
    {
        _options = options;
        _nyxClient = nyxClient;
    }

    public Task<string> SendMessageAsync(string token, TelegramSendMessageRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["chat_id"] = request.ChatId,
            ["text"] = request.Text,
        };
        if (!string.IsNullOrWhiteSpace(request.ParseMode))
            body["parse_mode"] = request.ParseMode;
        if (request.DisableNotification == true)
            body["disable_notification"] = true;
        if (request.ReplyToMessageId is { } replyTo)
            body["reply_to_message_id"] = replyTo;
        if (!string.IsNullOrWhiteSpace(request.ReplyMarkupJson))
        {
            try
            {
                body["reply_markup"] = JsonNode.Parse(request.ReplyMarkupJson);
            }
            catch (JsonException)
            {
                // Caller is responsible for valid JSON; surface as Telegram-side error if invalid.
                body["reply_markup"] = request.ReplyMarkupJson;
            }
        }

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            "sendMessage",
            "POST",
            body.ToJsonString(JsonOptions),
            extraHeaders: null,
            ct);
    }

    public Task<string> GetChatAsync(string token, TelegramGetChatRequest request, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            chat_id = request.ChatId,
        });

        return _nyxClient.ProxyRequestAsync(
            token,
            _options.ProviderSlug,
            "getChat",
            "POST",
            body,
            extraHeaders: null,
            ct);
    }
}
