using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class TelegramChannelNativeMessageSender : IChannelNativeMessageSender
{
    private readonly NyxIdApiClient _nyxIdApiClient;

    public TelegramChannelNativeMessageSender(NyxIdApiClient nyxIdApiClient)
    {
        _nyxIdApiClient = nyxIdApiClient ?? throw new ArgumentNullException(nameof(nyxIdApiClient));
    }

    public ChannelId Channel => ChannelId.From("telegram");

    public async Task SendAsync(
        UserAgentDeliveryTarget target,
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
        if (NyxApiResponseHelper.LooksLikeErrorEnvelope(response))
        {
            throw new InvalidOperationException(
                $"Telegram interaction notification delivery failed: {NyxApiResponseHelper.ExtractErrorDetail(response)}");
        }

        if (TryGetTelegramError(response, out var detail))
            throw new InvalidOperationException($"Telegram interaction notification delivery failed: {detail}");
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
