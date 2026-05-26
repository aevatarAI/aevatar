using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aevatar.AI.ToolProviders.Telegram;

internal static class TelegramProxyResponseParser
{
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, OutputOptions);

    /// <summary>
    /// Returns true and populates <paramref name="error"/> when the response is either a NyxID
    /// proxy error envelope or a Telegram Bot API <c>ok:false</c> response. Bot API responses use
    /// <c>{ok, result, error_code, description}</c>; NyxID wraps non-2xx HTTP into
    /// <c>{error:true, status, body, message}</c>.
    /// </summary>
    public static bool TryParseError(string? response, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
        {
            error = "empty_telegram_response";
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProp) && errorProp.ValueKind == JsonValueKind.True)
            {
                var status = TryReadInt(root, "status");
                var message = TryReadString(root, "message");
                var body = TryReadString(root, "body");
                error = $"nyx_proxy_error status={status?.ToString() ?? "unknown"}";
                if (!string.IsNullOrWhiteSpace(message))
                    error += $" message={message}";
                if (!string.IsNullOrWhiteSpace(body))
                    error += $" body={body}";
                return true;
            }

            if (root.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
            {
                var code = TryReadInt(root, "error_code");
                var description = TryReadString(root, "description");
                error = $"telegram_error_code={code?.ToString() ?? "unknown"}";
                if (!string.IsNullOrWhiteSpace(description))
                    error += $" description={description}";
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            error = "invalid_telegram_response_json";
            return true;
        }
    }

    /// <summary>
    /// Extracts useful identifiers from a successful Telegram <c>sendMessage</c> response. The
    /// shape is <c>{ok:true, result:{message_id, chat:{id,type}, date, ...}}</c>.
    /// </summary>
    public static TelegramSendMessageResult ParseSendSuccess(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return new TelegramSendMessageResult(MessageId: null, ChatId: null, Date: null);

            var messageId = TryReadInt(result, "message_id");
            var date = TryReadInt(result, "date");
            string? chatId = null;
            if (result.TryGetProperty("chat", out var chat) && chat.ValueKind == JsonValueKind.Object)
            {
                chatId = TryReadStringOrNumber(chat, "id");
            }

            return new TelegramSendMessageResult(messageId, chatId, date);
        }
        catch (JsonException)
        {
            return new TelegramSendMessageResult(MessageId: null, ChatId: null, Date: null);
        }
    }

    /// <summary>
    /// Extracts the chat block from a successful Telegram <c>getChat</c> response.
    /// </summary>
    public static TelegramChatInfo ParseChatSuccess(string response)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            if (!document.RootElement.TryGetProperty("result", out var chat) || chat.ValueKind != JsonValueKind.Object)
                return new TelegramChatInfo(Id: null, Type: null, Title: null, Username: null);

            return new TelegramChatInfo(
                Id: TryReadStringOrNumber(chat, "id"),
                Type: TryReadString(chat, "type"),
                Title: TryReadString(chat, "title"),
                Username: TryReadString(chat, "username"));
        }
        catch (JsonException)
        {
            return new TelegramChatInfo(Id: null, Type: null, Title: null, Username: null);
        }
    }

    private static int? TryReadInt(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value
            : null;

    private static string? TryReadString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string? TryReadStringOrNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop))
            return null;

        return prop.ValueKind switch
        {
            JsonValueKind.String => prop.GetString(),
            JsonValueKind.Number => prop.ToString(),
            _ => null,
        };
    }
}

internal sealed record TelegramSendMessageResult(int? MessageId, string? ChatId, int? Date);

internal sealed record TelegramChatInfo(string? Id, string? Type, string? Title, string? Username);
