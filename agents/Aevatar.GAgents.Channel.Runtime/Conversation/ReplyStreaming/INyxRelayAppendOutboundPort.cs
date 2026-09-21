using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>Formats and attempts one Actor-selected Relay append operation.</summary>
public interface INyxRelayAppendOutboundPort
{
    /// <summary>Returns the complete platform text without trimming or truncation.</summary>
    string PrepareText(string platform, ConversationReference conversation, string rawText);

    /// <summary>
    /// Resolves credentials and validates the fixed segment before invoking the dispatch fence.
    /// Invokes onDispatch on the caller's Actor scheduler and awaits its commit before sending HTTP.
    /// Once the fence is invoked, uncertain outcomes never permit another attempt.
    /// </summary>
    Task<NyxRelayAppendSendResult> SendAsync(
        ChatActivity activity,
        string rawText,
        int maxLength,
        Func<CancellationToken, Task> onDispatch,
        CancellationToken ct);
}

public enum NyxRelayAppendSendState
{
    PreDispatchFailure,
    Accepted,
    Rejected,
    DeliveryUnknown,
}

public sealed record NyxRelayAppendSendResult(
    NyxRelayAppendSendState State,
    string PlatformMessageId = "",
    string ErrorCode = "",
    int HttpStatus = 0);
