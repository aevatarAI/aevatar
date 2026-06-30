namespace Aevatar.GAgents.Platform.Lark.Abstractions;

/// <summary>
/// Narrow boundary for posting new outbound Lark messages from actor-owned execution paths.
/// </summary>
public interface ILarkOutboundDispatcher
{
    Task<LarkSendNewMessageResult> SendNewMessageAsync(
        LarkSendNewMessageRequest request,
        CancellationToken ct);
}

public sealed record LarkSendNewMessageRequest(
    string NyxProxyBearerToken,
    string NyxProviderSlug,
    string MessageType,
    string ContentJson,
    LarkReceiveTarget PrimaryTarget,
    LarkReceiveTarget? FallbackTarget = null);

public sealed record LarkSendNewMessageResult(
    bool Succeeded,
    string? MessageId,
    LarkReceiveTarget AttemptedTarget,
    bool UsedFallback,
    int? LarkCode,
    string Detail)
{
    public static LarkSendNewMessageResult Sent(
        string messageId,
        LarkReceiveTarget attemptedTarget,
        bool usedFallback) =>
        new(
            Succeeded: true,
            MessageId: messageId,
            AttemptedTarget: attemptedTarget,
            UsedFallback: usedFallback,
            LarkCode: null,
            Detail: string.Empty);

    public static LarkSendNewMessageResult Failed(
        LarkReceiveTarget attemptedTarget,
        bool usedFallback,
        int? larkCode,
        string detail) =>
        new(
            Succeeded: false,
            MessageId: null,
            AttemptedTarget: attemptedTarget,
            UsedFallback: usedFallback,
            LarkCode: larkCode,
            Detail: detail);
}
