using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

public sealed record NyxIdRelayParseResult(
    bool Success,
    bool Ignored,
    NyxIdRelayCallbackPayload? Payload,
    ChatActivity? Activity,
    string ErrorCode,
    string ErrorSummary)
{
    public static NyxIdRelayParseResult Parsed(NyxIdRelayCallbackPayload payload, ChatActivity activity) =>
        new(true, false, payload, activity, string.Empty, string.Empty);

    public static NyxIdRelayParseResult Invalid(string errorCode, string errorSummary) =>
        new(false, false, null, null, errorCode, errorSummary);

    public static NyxIdRelayParseResult IgnoredPayload(NyxIdRelayCallbackPayload? payload, string errorCode, string errorSummary) =>
        new(false, true, payload, null, errorCode, errorSummary);
}
