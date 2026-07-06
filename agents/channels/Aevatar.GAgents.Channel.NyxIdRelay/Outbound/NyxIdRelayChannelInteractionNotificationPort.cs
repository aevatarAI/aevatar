using System.Text.Json;
using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;
using Microsoft.Extensions.Logging;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class NyxIdRelayChannelInteractionNotificationPort : IChannelInteractionNotificationPort
{
    private const int LarkOpenIdCrossApp = 99992361;
    private const int LarkUserIdCrossTenant = 99992364;
    private const int LarkBotNotInChat = 230002;

    private readonly IUserAgentDeliveryTargetReader _deliveryTargetReader;
    private readonly NyxIdApiClient _nyxIdApiClient;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageProducer> _nativeProducers;
    private readonly ILogger<NyxIdRelayChannelInteractionNotificationPort> _logger;

    public NyxIdRelayChannelInteractionNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        IEnumerable<IChannelNativeMessageProducer> nativeProducers,
        ILogger<NyxIdRelayChannelInteractionNotificationPort> logger)
    {
        _deliveryTargetReader = deliveryTargetReader ?? throw new ArgumentNullException(nameof(deliveryTargetReader));
        _nyxIdApiClient = nyxIdApiClient ?? throw new ArgumentNullException(nameof(nyxIdApiClient));
        ArgumentNullException.ThrowIfNull(nativeProducers);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var producers = new Dictionary<string, IChannelNativeMessageProducer>(StringComparer.OrdinalIgnoreCase);
        foreach (var producer in nativeProducers)
        {
            ArgumentNullException.ThrowIfNull(producer);
            var key = NormalizePlatform(producer.Channel.Value);
            if (!producers.TryAdd(key, producer))
            {
                throw new InvalidOperationException(
                    $"Multiple native message producers are registered for platform '{key}'.");
            }
        }

        _nativeProducers = producers;
    }

    public async Task DeliverAsync(
        ChannelInteractionNotificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var target = await ResolveTargetAsync(request.DeliveryTargetId, cancellationToken).ConfigureAwait(false);
        var platform = NormalizePlatform(target.Platform);
        if (!_nativeProducers.TryGetValue(platform, out var producer))
            throw new NotSupportedException($"No channel message producer is registered for platform: {target.Platform}");

        var content = HumanInteractionMessageMapper.ToMessageContent(request);
        var nativeMessage = ProduceNativeMessage(producer, content, target);

        switch (platform)
        {
            case "lark":
                await SendLarkAsync(target, nativeMessage, cancellationToken).ConfigureAwait(false);
                break;
            case "telegram":
                await SendTelegramAsync(target, nativeMessage, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new NotSupportedException($"Unsupported interaction notification platform: {target.Platform}");
        }

        _logger.LogInformation(
            "Delivered channel interaction notification: target={DeliveryTargetId}, platform={Platform}, run={RunId}, step={StepId}, capability={Capability}",
            request.DeliveryTargetId,
            target.Platform,
            request.RunId,
            request.StepId,
            nativeMessage.Capability);
    }

    private async Task<UserAgentDeliveryTarget> ResolveTargetAsync(
        string deliveryTargetId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(deliveryTargetId))
            throw new InvalidOperationException("Interaction notification delivery target id is required.");

        var target = await _deliveryTargetReader.GetAsync(deliveryTargetId, cancellationToken).ConfigureAwait(false);
        if (target is null)
            throw new InvalidOperationException($"Agent delivery target not found: {deliveryTargetId}");

        if (string.IsNullOrWhiteSpace(target.Platform))
            throw new InvalidOperationException($"Agent delivery target platform is missing: {deliveryTargetId}");

        return target;
    }

    private static ChannelNativeMessage ProduceNativeMessage(
        IChannelNativeMessageProducer producer,
        MessageContent content,
        UserAgentDeliveryTarget target)
    {
        var context = new ComposeContext
        {
            Conversation = new ConversationReference
            {
                CanonicalKey = $"{NormalizePlatform(target.Platform)}:{target.ConversationId}",
            },
        };
        var capability = producer.Evaluate(content, context);
        if (capability == ComposeCapability.Unsupported)
        {
            throw new NotSupportedException(
                $"Channel producer for platform '{target.Platform}' cannot express the requested interaction notification.");
        }

        var nativeMessage = producer.Produce(content, context);
        if (nativeMessage.Capability == ComposeCapability.Unsupported)
        {
            throw new NotSupportedException(
                $"Channel producer for platform '{target.Platform}' produced an unsupported interaction notification.");
        }

        return nativeMessage;
    }

    private async Task SendLarkAsync(
        UserAgentDeliveryTarget target,
        ChannelNativeMessage nativeMessage,
        CancellationToken cancellationToken)
    {
        var primaryTarget = ResolveLarkPrimaryTarget(target);
        var fallbackTarget = ResolveLarkFallbackTarget(target);
        var result = await SendLarkToTargetAsync(
            target,
            nativeMessage,
            primaryTarget,
            usedFallback: false,
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded &&
            result.LarkCode == LarkBotNotInChat &&
            fallbackTarget is { } fallback)
        {
            _logger.LogInformation(
                "Lark interaction notification primary target rejected as bot-not-in-chat; retrying once with fallback receive_id_type={FallbackType}",
                fallback.ReceiveIdType);
            result = await SendLarkToTargetAsync(
                target,
                nativeMessage,
                fallback,
                usedFallback: true,
                cancellationToken).ConfigureAwait(false);
        }

        if (!result.Succeeded)
            throw new InvalidOperationException(BuildLarkRejectionMessage(result.LarkCode, result.Detail));
    }

    private async Task<LarkSendResult> SendLarkToTargetAsync(
        UserAgentDeliveryTarget target,
        ChannelNativeMessage nativeMessage,
        LarkReceiveTarget targetAddress,
        bool usedFallback,
        CancellationToken cancellationToken)
    {
        var contentJson = nativeMessage.CardPayload is null
            ? JsonSerializer.Serialize(new { text = nativeMessage.Text ?? string.Empty })
            : SerializeNativePayload(nativeMessage.CardPayload);
        var messageType = string.IsNullOrWhiteSpace(nativeMessage.MessageType)
            ? nativeMessage.CardPayload is null ? "text" : "interactive"
            : nativeMessage.MessageType;
        var body = JsonSerializer.Serialize(new
        {
            receive_id = targetAddress.ReceiveId,
            msg_type = messageType,
            content = contentJson,
        });

        var response = await _nyxIdApiClient.ProxyRequestAsync(
            target.NyxApiKey,
            target.NyxProviderSlug,
            $"open-apis/im/v1/messages?receive_id_type={Uri.EscapeDataString(targetAddress.ReceiveIdType)}",
            "POST",
            body,
            extraHeaders: null,
            cancellationToken).ConfigureAwait(false);

        return ParseLarkSendResponse(response, targetAddress, usedFallback);
    }

    private async Task SendTelegramAsync(
        UserAgentDeliveryTarget target,
        ChannelNativeMessage nativeMessage,
        CancellationToken cancellationToken)
    {
        var text = nativeMessage.Text;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Telegram interaction notification requires a non-empty text payload.");

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["chat_id"] = target.ConversationId,
            ["text"] = text,
            ["parse_mode"] = "Markdown",
        };

        if (nativeMessage.CardPayload is not null)
        {
            using var document = JsonDocument.Parse(SerializeNativePayload(nativeMessage.CardPayload));
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

    private static LarkSendResult ParseLarkSendResponse(
        string? response,
        LarkReceiveTarget target,
        bool usedFallback)
    {
        if (TryGetLarkError(response, out var larkCode, out var detail))
            return LarkSendResult.Failed(target, usedFallback, larkCode, detail);

        if (string.IsNullOrWhiteSpace(response))
            return LarkSendResult.Failed(target, usedFallback, null, "empty_send_response");

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return LarkSendResult.Failed(target, usedFallback, null, "missing_data");
            if (!data.TryGetProperty("message_id", out var idProperty) ||
                idProperty.ValueKind != JsonValueKind.String)
            {
                return LarkSendResult.Failed(target, usedFallback, null, "missing_message_id");
            }

            var messageId = idProperty.GetString();
            return string.IsNullOrWhiteSpace(messageId)
                ? LarkSendResult.Failed(target, usedFallback, null, "empty_message_id")
                : LarkSendResult.Sent(messageId, target, usedFallback);
        }
        catch (JsonException)
        {
            return LarkSendResult.Failed(target, usedFallback, null, "invalid_send_response_json");
        }
    }

    private static bool TryGetLarkError(string? response, out int? larkCode, out string detail)
    {
        larkCode = null;
        detail = string.Empty;
        if (string.IsNullOrWhiteSpace(response))
            return false;

        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            if (root.TryGetProperty("code", out var topCodeProperty) &&
                topCodeProperty.ValueKind == JsonValueKind.Number &&
                topCodeProperty.TryGetInt32(out var topCode) &&
                topCode != 0)
            {
                larkCode = topCode;
                detail = TryReadString(root, "msg") ?? $"code={topCode}";
                return true;
            }

            if (!root.TryGetProperty("error", out var errorProperty))
                return false;

            var hasErrorFlag = errorProperty.ValueKind == JsonValueKind.True ||
                               (errorProperty.ValueKind == JsonValueKind.String &&
                                !string.IsNullOrWhiteSpace(errorProperty.GetString()));
            if (!hasErrorFlag)
                return false;

            if (TryParseNestedLarkBody(root, out larkCode, out detail))
                return true;

            detail = errorProperty.ValueKind == JsonValueKind.String
                ? errorProperty.GetString()!.Trim()
                : TryReadString(root, "message") ?? TryReadString(root, "body") ?? "proxy_error";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseNestedLarkBody(JsonElement root, out int? larkCode, out string detail)
    {
        larkCode = null;
        detail = string.Empty;
        var rawBody = TryReadString(root, "body");
        if (string.IsNullOrEmpty(rawBody))
            return false;

        try
        {
            using var nested = JsonDocument.Parse(rawBody);
            var nestedRoot = nested.RootElement;
            if (nestedRoot.ValueKind != JsonValueKind.Object ||
                !nestedRoot.TryGetProperty("code", out var codeProperty) ||
                codeProperty.ValueKind != JsonValueKind.Number ||
                !codeProperty.TryGetInt32(out var code) ||
                code == 0)
            {
                return false;
            }

            larkCode = code;
            var msg = TryReadString(nestedRoot, "msg") ?? $"code={code}";
            detail = root.TryGetProperty("status", out var statusProperty) &&
                     statusProperty.ValueKind == JsonValueKind.Number &&
                     statusProperty.TryGetInt32(out var status)
                ? $"nyx_status={status} lark_code={code} msg={msg}"
                : $"lark_code={code} msg={msg}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string SerializeNativePayload(object payload) =>
        payload is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(payload);

    private static LarkReceiveTarget ResolveLarkPrimaryTarget(UserAgentDeliveryTarget target)
    {
        var receiveId = FirstNonWhiteSpace(target.LarkReceiveId, target.ConversationId);
        if (string.IsNullOrWhiteSpace(receiveId))
            throw new InvalidOperationException($"Lark delivery target receive_id is missing: {target.AgentId}");

        var receiveIdType = string.IsNullOrWhiteSpace(target.LarkReceiveIdType)
            ? InferLarkReceiveIdType(receiveId)
            : target.LarkReceiveIdType.Trim();
        return new LarkReceiveTarget(receiveId.Trim(), receiveIdType);
    }

    private static LarkReceiveTarget? ResolveLarkFallbackTarget(UserAgentDeliveryTarget target)
    {
        var fallbackId = target.LarkReceiveIdFallback?.Trim();
        var fallbackType = target.LarkReceiveIdTypeFallback?.Trim();
        return string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType)
            ? null
            : new LarkReceiveTarget(fallbackId, fallbackType);
    }

    private static string InferLarkReceiveIdType(string receiveId)
    {
        if (receiveId.StartsWith("oc_", StringComparison.Ordinal))
            return "chat_id";
        if (receiveId.StartsWith("on_", StringComparison.Ordinal))
            return "union_id";

        return "open_id";
    }

    private static string BuildLarkRejectionMessage(int? larkCode, string detail)
    {
        const string failurePrefix = "Channel interaction notification delivery failed";
        if (larkCode == LarkOpenIdCrossApp)
        {
            return
                $"{failurePrefix} (code={larkCode}): {detail}. " +
                "This agent was created before cross-app union_id ingress existed; " +
                "delete it (`/agents` -> Delete) and recreate it to pick up the cross-app safe target.";
        }

        if (larkCode == LarkUserIdCrossTenant)
        {
            return
                $"{failurePrefix} (code={larkCode}): {detail}. " +
                "The outbound Lark app is in a different tenant than the inbound app, so " +
                "user-id translation is impossible. Delete the agent (`/agents` -> Delete) and recreate " +
                "it so the new chat_id-preferred outbound path takes effect, or align the NyxID " +
                "`s/api-lark-bot` proxy with the channel-bot that received the inbound event.";
        }

        return larkCode is { } code
            ? $"{failurePrefix} (code={code}): {detail}"
            : $"{failurePrefix}: {detail}";
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

    private static string FirstNonWhiteSpace(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizePlatform(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private sealed record LarkReceiveTarget(string ReceiveId, string ReceiveIdType);

    private sealed record LarkSendResult(
        bool Succeeded,
        string? MessageId,
        LarkReceiveTarget Target,
        bool UsedFallback,
        int? LarkCode,
        string Detail)
    {
        public static LarkSendResult Sent(string messageId, LarkReceiveTarget target, bool usedFallback) =>
            new(true, messageId, target, usedFallback, null, string.Empty);

        public static LarkSendResult Failed(
            LarkReceiveTarget target,
            bool usedFallback,
            int? larkCode,
            string detail) =>
            new(false, null, target, usedFallback, larkCode, detail);
    }
}
