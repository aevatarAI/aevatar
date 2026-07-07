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

    private readonly IUserAgentDeliveryTargetReader _deliveryTargetReader;
    private readonly NyxIdApiClient _nyxIdApiClient;
    private readonly ILarkOutboundRelayDispatcher _larkOutboundDispatcher;
    private readonly IReadOnlyDictionary<string, IChannelNativeMessageProducer> _nativeProducers;
    private readonly ILogger<NyxIdRelayChannelInteractionNotificationPort> _logger;

    public NyxIdRelayChannelInteractionNotificationPort(
        IUserAgentDeliveryTargetReader deliveryTargetReader,
        NyxIdApiClient nyxIdApiClient,
        ILarkOutboundRelayDispatcher larkOutboundDispatcher,
        IEnumerable<IChannelNativeMessageProducer> nativeProducers,
        ILogger<NyxIdRelayChannelInteractionNotificationPort> logger)
    {
        _deliveryTargetReader = deliveryTargetReader ?? throw new ArgumentNullException(nameof(deliveryTargetReader));
        _nyxIdApiClient = nyxIdApiClient ?? throw new ArgumentNullException(nameof(nyxIdApiClient));
        _larkOutboundDispatcher = larkOutboundDispatcher ?? throw new ArgumentNullException(nameof(larkOutboundDispatcher));
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
        var contentJson = nativeMessage.CardPayload is null
            ? JsonSerializer.Serialize(new { text = nativeMessage.Text ?? string.Empty })
            : SerializeNativePayload(nativeMessage.CardPayload);
        var messageType = string.IsNullOrWhiteSpace(nativeMessage.MessageType)
            ? nativeMessage.CardPayload is null ? "text" : "interactive"
            : nativeMessage.MessageType;

        var result = await _larkOutboundDispatcher.SendNewMessageAsync(
            new LarkOutboundRelayRequest(
                target.NyxApiKey,
                target.NyxProviderSlug,
                messageType,
                contentJson,
                primaryTarget.ReceiveId,
                primaryTarget.ReceiveIdType,
                fallbackTarget?.ReceiveId,
                fallbackTarget?.ReceiveIdType),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new InvalidOperationException(BuildLarkRejectionMessage(result.LarkCode, result.Detail));
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
}
