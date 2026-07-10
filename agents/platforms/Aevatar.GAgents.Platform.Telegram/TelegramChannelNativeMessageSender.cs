using System.Globalization;
using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.Foundation.Abstractions.Helpers;

namespace Aevatar.GAgents.Platform.Telegram;

public sealed class TelegramChannelNativeMessageSender : IChannelNativeMessageSender
{
    private readonly NyxIdApiClient _nyxIdApiClient;

    public TelegramChannelNativeMessageSender(NyxIdApiClient nyxIdApiClient)
    {
        _nyxIdApiClient = nyxIdApiClient ?? throw new ArgumentNullException(nameof(nyxIdApiClient));
    }

    public ChannelId Channel => ChannelId.From("telegram");

    public async Task<EmitResult> SendAsync(
        ChannelNativeDeliveryTarget target,
        ChannelNativeMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);

        var text = message.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Telegram interaction notification requires a non-empty text payload.");

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["chat_id"] = target.ConversationId,
            ["text"] = text,
            ["parse_mode"] = "Markdown",
        };

        if (message.CardPayload is not null)
        {
            using var document = JsonDocument.Parse(SerializeNativePayload(message.CardPayload));
            if (document.RootElement.TryGetProperty("reply_markup", out var replyMarkup))
                body["reply_markup"] = replyMarkup.Clone();
        }

        var response = await _nyxIdApiClient.ProxyRequestAsync(
            target.NyxApiKey,
            target.NyxProviderSlug,
            "sendMessage",
            "POST",
            JsonSerializer.Serialize(body),
            extraHeaders: null,
            cancellationToken).ConfigureAwait(false);
        if (LooksLikeErrorEnvelope(response))
        {
            throw new InvalidOperationException(
                $"Telegram interaction notification delivery failed: {ExtractErrorDetail(response)}");
        }

        if (TryGetTelegramError(response, out var detail))
            throw new InvalidOperationException($"Telegram interaction notification delivery failed: {detail}");

        return EmitResult.Sent(
            TryReadTelegramMessageId(response) ?? $"telegram:{target.ConversationId}");
    }

    public async Task<EmitResult> UpdateAsync(
        ChannelNativeDeliveryTarget target,
        string platformMessageId,
        ChannelNativeMessage message,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        _ = isFinal;
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(platformMessageId))
            throw new InvalidOperationException("Telegram native message update requires a platform message id.");

        var text = message.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Telegram interaction notification update requires a non-empty text payload.");

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["chat_id"] = target.ConversationId,
            ["message_id"] = NormalizeTelegramMessageId(platformMessageId),
            ["text"] = text,
            ["parse_mode"] = "Markdown",
        };

        if (message.CardPayload is not null)
        {
            using var document = JsonDocument.Parse(SerializeNativePayload(message.CardPayload));
            if (document.RootElement.TryGetProperty("reply_markup", out var replyMarkup))
                body["reply_markup"] = replyMarkup.Clone();
        }

        var response = await _nyxIdApiClient.ProxyRequestAsync(
            target.NyxApiKey,
            target.NyxProviderSlug,
            "editMessageText",
            "POST",
            JsonSerializer.Serialize(body),
            extraHeaders: null,
            cancellationToken).ConfigureAwait(false);
        if (LooksLikeErrorEnvelope(response))
        {
            throw new InvalidOperationException(
                $"Telegram interaction notification update failed: {ExtractErrorDetail(response)}");
        }

        if (TryGetTelegramError(response, out var detail))
            throw new InvalidOperationException($"Telegram interaction notification update failed: {detail}");

        return EmitResult.Sent(
            TryReadTelegramMessageId(response) ?? platformMessageId.Trim(),
            platformMessageId: platformMessageId.Trim());
    }

    private static string? TryReadTelegramMessageId(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Object ||
                !result.TryGetProperty("message_id", out var messageId))
            {
                return null;
            }

            return messageId.ValueKind switch
            {
                JsonValueKind.Number when messageId.TryGetInt64(out var id) => id.ToString(CultureInfo.InvariantCulture),
                JsonValueKind.String => messageId.GetString()?.Trim(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object NormalizeTelegramMessageId(string value)
    {
        var trimmed = value.Trim();
        return long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : trimmed;
    }

    private static bool TryGetTelegramError(string? response, out string detail)
    {
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
        {
            detail = "empty_send_response";
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("ok", out var okProperty) && okProperty.ValueKind == JsonValueKind.False)
            {
                var errorCode = root.TryGetProperty("error_code", out var errorCodeProperty) &&
                                errorCodeProperty.ValueKind == JsonValueKind.Number &&
                                errorCodeProperty.TryGetInt32(out var code)
                    ? $"telegram_code={code} "
                    : string.Empty;
                var description = TryReadString(root, "description") ?? "telegram_ok_false";
                detail = $"{errorCode}{description}".Trim();
                return true;
            }

            var rawBody = TryReadString(root, "body");
            if (string.IsNullOrWhiteSpace(rawBody))
                return false;

            return TryGetTelegramError(rawBody, out detail);
        }
        catch (JsonException)
        {
            detail = "invalid_send_response_json";
            return true;
        }
    }

    private static string SerializeNativePayload(object payload) =>
        payload is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(payload);

    private static bool LooksLikeErrorEnvelope(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return true;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("error", out var errorProp) &&
                   errorProp.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static string ExtractErrorDetail(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return "empty_response";

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var status = root.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.Number
                ? statusElement.GetInt32().ToString()
                : "unknown";
            var body = root.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind == JsonValueKind.String
                ? bodyElement.GetString()
                : string.Empty;
            var message = root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? messageElement.GetString()
                : string.Empty;

            return $"nyx_status={status}" +
                   (string.IsNullOrWhiteSpace(body) ? string.Empty : $" body={SecretScrubber.Scrub(body)}") +
                   (string.IsNullOrWhiteSpace(message) ? string.Empty : $" message={SecretScrubber.Scrub(message)}");
        }
        catch (JsonException)
        {
            return "invalid_error_envelope";
        }
    }

    private static string? TryReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = property.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
