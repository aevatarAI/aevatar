using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

public interface IChannelRelayTailTextSender
{
    Task<ChannelRelayTailTextSendResult> SendTailSegmentsAsync(
        ChannelRelayTailTextSendRequest request,
        CancellationToken cancellationToken);
}

public sealed record ChannelRelayTailTextSendRequest(
    string Platform,
    string ChatType,
    string ConversationId,
    string SenderId,
    TransportExtras? TransportExtras,
    string NyxProxyCredential,
    IReadOnlyList<string> TailSegments,
    string CorrelationId);

public sealed record ChannelRelayTailTextSendResult(
    bool Succeeded,
    string ErrorCode,
    string Detail,
    FailureKind FailureKind = FailureKind.PermanentAdapterError,
    int RawErrorCode = 0)
{
    public static ChannelRelayTailTextSendResult Success() =>
        new(true, string.Empty, string.Empty);

    public static ChannelRelayTailTextSendResult Failed(
        string errorCode,
        string detail,
        FailureKind failureKind = FailureKind.PermanentAdapterError,
        int rawErrorCode = 0) =>
        new(false, errorCode, detail, failureKind, rawErrorCode);
}
