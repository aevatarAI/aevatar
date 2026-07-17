namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Narrow boundary for posting new outbound Lark messages from actor-owned execution paths.
/// </summary>
/// <remarks>
/// Refactor (iter166/cluster-415-lark-outbound-dispatcher):
///   Old pattern: scheduled automation and Lark card senders each owned their own Lark POST parser/fallback branch.
///   New principle: one dispatcher owns new-message POST, primary/fallback retry, and response parsing while callers keep only target/content mapping.
///
/// This interface is intentionally narrow even with one production implementation: actor and
/// human-interaction callers depend on the send contract, while tests can substitute transport
/// outcomes without reaching into NyxID proxy HTTP details.
/// </remarks>
public interface ILarkOutboundDispatcher
{
    Task<LarkSendNewMessageResult> SendNewMessageAsync(
        LarkSendNewMessageRequest request,
        CancellationToken ct);

    Task<LarkUpdateMessageResult> UpdateMessageAsync(
        LarkUpdateMessageRequest request,
        CancellationToken ct);
}

public sealed record LarkSendNewMessageRequest(
    string NyxApiKey,
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

public sealed record LarkUpdateMessageRequest(
    string NyxApiKey,
    string NyxProviderSlug,
    string MessageId,
    string MessageType,
    string ContentJson);

public sealed record LarkUpdateMessageResult(
    bool Succeeded,
    string? MessageId,
    int? LarkCode,
    string Detail)
{
    public static LarkUpdateMessageResult Updated(string messageId) =>
        new(
            Succeeded: true,
            MessageId: messageId,
            LarkCode: null,
            Detail: string.Empty);

    public static LarkUpdateMessageResult Failed(int? larkCode, string detail) =>
        new(
            Succeeded: false,
            MessageId: null,
            LarkCode: larkCode,
            Detail: detail);
}
