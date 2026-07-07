using System.Text.Json;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Scheduled;

namespace Aevatar.GAgents.Channel.NyxIdRelay.Outbound;

public sealed class LarkChannelNativeMessageSender : IChannelNativeMessageSender
{
    private const int LarkOpenIdCrossApp = 99992361;
    private const int LarkUserIdCrossTenant = 99992364;

    private readonly ILarkOutboundRelayDispatcher _larkOutboundDispatcher;

    public LarkChannelNativeMessageSender(ILarkOutboundRelayDispatcher larkOutboundDispatcher)
    {
        _larkOutboundDispatcher = larkOutboundDispatcher ?? throw new ArgumentNullException(nameof(larkOutboundDispatcher));
    }

    public ChannelId Channel => ChannelId.From("lark");

    public async Task SendAsync(
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
            throw new InvalidOperationException(BuildRejectionMessage(result.LarkCode, result.Detail));
    }

    private static string SerializeNativePayload(object payload) =>
        payload is JsonElement element ? element.GetRawText() : JsonSerializer.Serialize(payload);

    private static LarkReceiveTarget ResolvePrimaryTarget(ChannelNativeDeliveryTarget target)
    {
        var receiveId = FirstNonWhiteSpace(target.LarkReceiveId, target.ConversationId);
        if (string.IsNullOrWhiteSpace(receiveId))
            throw new InvalidOperationException($"Lark delivery target receive_id is missing: {target.AgentId}");

        var receiveIdType = string.IsNullOrWhiteSpace(target.LarkReceiveIdType)
            ? InferReceiveIdType(receiveId)
            : target.LarkReceiveIdType.Trim();
        return new LarkReceiveTarget(receiveId.Trim(), receiveIdType);
    }

    private static LarkReceiveTarget? ResolveFallbackTarget(ChannelNativeDeliveryTarget target)
    {
        var fallbackId = target.LarkReceiveIdFallback?.Trim();
        var fallbackType = target.LarkReceiveIdTypeFallback?.Trim();
        return string.IsNullOrEmpty(fallbackId) || string.IsNullOrEmpty(fallbackType)
            ? null
            : new LarkReceiveTarget(fallbackId, fallbackType);
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

    private sealed record LarkReceiveTarget(string ReceiveId, string ReceiveIdType);
}
