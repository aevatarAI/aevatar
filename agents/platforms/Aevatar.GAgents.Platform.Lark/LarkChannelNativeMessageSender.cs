using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkChannelNativeMessageSender : IChannelNativeMessageSender
{
    private const int LarkOpenIdCrossApp = 99992361;
    private const int LarkUserIdCrossTenant = 99992364;
    private const int LarkEditCapReachedCode = 230072;
    private const string MessageUpdateSealedErrorCode = "message_update_sealed";

    private readonly ILarkOutboundDispatcher _larkOutboundDispatcher;

    public LarkChannelNativeMessageSender(ILarkOutboundDispatcher larkOutboundDispatcher)
    {
        _larkOutboundDispatcher = larkOutboundDispatcher ?? throw new ArgumentNullException(nameof(larkOutboundDispatcher));
    }

    public ChannelId Channel => ChannelId.From("lark");

    public async Task<EmitResult> SendAsync(
        ChannelNativeDeliveryTarget target,
        ChannelNativeMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);

        var primaryTarget = ResolvePrimaryTarget(target);
        var fallbackTarget = ResolveFallbackTarget(target);
        var contentJson = message.CardPayload is null
            ? JsonSerializer.Serialize(new { text = message.Text ?? string.Empty })
            : SerializeNativePayload(message.CardPayload);
        var messageType = string.IsNullOrWhiteSpace(message.MessageType)
            ? message.CardPayload is null ? "text" : "interactive"
            : message.MessageType;

        var result = await _larkOutboundDispatcher.SendNewMessageAsync(
            new LarkSendNewMessageRequest(
                target.NyxApiKey,
                target.NyxProviderSlug,
                messageType,
                contentJson,
                new LarkReceiveTarget(
                    primaryTarget.ReceiveId,
                    primaryTarget.ReceiveIdType,
                    FellBackToPrefixInference: false),
                fallbackTarget is null
                    ? null
                    : new LarkReceiveTarget(
                        fallbackTarget.ReceiveId,
                        fallbackTarget.ReceiveIdType,
                        FellBackToPrefixInference: false)),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new InvalidOperationException(BuildRejectionMessage(result.LarkCode, result.Detail));

        return EmitResult.Sent(
            result.MessageId ?? $"lark:{primaryTarget.ReceiveId}",
            platformMessageId: result.MessageId);
    }

    public async Task<EmitResult> UpdateAsync(
        ChannelNativeDeliveryTarget target,
        string platformMessageId,
        ChannelNativeMessage message,
        bool isFinal,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(platformMessageId))
            throw new InvalidOperationException("Lark native message update requires a platform message id.");
        if (message.CardPayload is not null)
            throw new NotSupportedException("Lark native message update currently supports text messages only.");

        var contentJson = JsonSerializer.Serialize(new { text = message.Text ?? string.Empty });
        var messageType = string.IsNullOrWhiteSpace(message.MessageType)
            ? "text"
            : message.MessageType;

        var result = await _larkOutboundDispatcher.UpdateMessageAsync(
            new LarkUpdateMessageRequest(
                target.NyxApiKey,
                target.NyxProviderSlug,
                platformMessageId.Trim(),
                messageType,
                contentJson),
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            if (result.LarkCode == LarkEditCapReachedCode)
            {
                if (isFinal)
                    return await SendAsync(target, message, cancellationToken).ConfigureAwait(false);

                return EmitResult.Failed(
                    MessageUpdateSealedErrorCode,
                    BuildRejectionMessage(result.LarkCode, result.Detail),
                    retryAfter: null,
                    capability: ComposeCapability.Unsupported,
                    failureKind: FailureKind.PermanentAdapterError,
                    rawErrorCode: result.LarkCode.GetValueOrDefault());
            }

            throw new InvalidOperationException(BuildRejectionMessage(result.LarkCode, result.Detail));
        }

        var messageId = result.MessageId ?? platformMessageId.Trim();
        return EmitResult.Sent(messageId, platformMessageId: messageId);
    }

    private static string SerializeNativePayload(object payload) =>
        payload switch
        {
            JsonElement element => element.GetRawText(),
            string rawJson => rawJson,
            _ => JsonSerializer.Serialize(payload),
        };

    private static NativeLarkReceiveTarget ResolvePrimaryTarget(ChannelNativeDeliveryTarget target)
    {
        var address = (target as IChannelDeliveryAddressTarget)?.ChannelAddress;
        var route = target as ILarkChannelNativeDeliveryRoute;
        var receiveId = FirstNonWhiteSpace(address?.Primary.AddressId, route?.LarkReceiveId, target.ConversationId);
        if (string.IsNullOrWhiteSpace(receiveId))
            throw new InvalidOperationException($"Lark delivery target receive_id is missing: {target.AgentId}");

        var explicitReceiveIdType = FirstNonWhiteSpace(address?.Primary.AddressType, route?.LarkReceiveIdType);
        var receiveIdType = string.IsNullOrWhiteSpace(explicitReceiveIdType)
            ? InferReceiveIdType(receiveId)
            : explicitReceiveIdType.Trim();
        return new NativeLarkReceiveTarget(receiveId.Trim(), receiveIdType);
    }

    private static NativeLarkReceiveTarget? ResolveFallbackTarget(ChannelNativeDeliveryTarget target)
    {
        var address = (target as IChannelDeliveryAddressTarget)?.ChannelAddress;
        var route = target as ILarkChannelNativeDeliveryRoute;

        var fallbackId = FirstNonWhiteSpace(address?.Fallback?.AddressId, route?.LarkReceiveIdFallback).Trim();
        var fallbackType = FirstNonWhiteSpace(address?.Fallback?.AddressType, route?.LarkReceiveIdTypeFallback).Trim();
        return string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType)
            ? null
            : new NativeLarkReceiveTarget(fallbackId, fallbackType);
    }

    private static string InferReceiveIdType(string receiveId)
    {
        if (receiveId.StartsWith("oc_", StringComparison.Ordinal))
            return "chat_id";
        if (receiveId.StartsWith("on_", StringComparison.Ordinal))
            return "union_id";

        return "open_id";
    }

    private static string BuildRejectionMessage(int? larkCode, string detail)
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

    private static string FirstNonWhiteSpace(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private sealed record NativeLarkReceiveTarget(string ReceiveId, string ReceiveIdType);
}
