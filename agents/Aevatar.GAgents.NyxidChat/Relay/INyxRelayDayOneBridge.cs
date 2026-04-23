namespace Aevatar.GAgents.NyxidChat.Relay;

/// <summary>
/// Deterministic Day One slash-command bridge for Nyx relay callbacks.
///
/// The bridge is a disposable helper for the Nyx relay production path. It lets the
/// endpoint resolve Day One text commands (<c>/daily</c>, <c>/social-media</c>,
/// <c>/agents</c>, …) without going through the LLM, while free-text still falls
/// through to the existing chat agent. Reply capability is scoped to the current
/// callback token — the bridge must not introduce long-term state, durable
/// reply-token cache, or any outbound credential persistence.
/// </summary>
public interface INyxRelayDayOneBridge
{
    /// <summary>
    /// Synchronous check: should the bridge own this message instead of the
    /// LLM path? Returns true for slash-prefixed text in non-device conversations.
    /// </summary>
    bool ShouldHandle(NyxRelayBridgeRequest request);

    /// <summary>
    /// Resolve the deterministic reply text for a bridge-owned message. The
    /// caller is responsible for delivering the result via the current
    /// callback's relay reply channel.
    /// </summary>
    Task<string> HandleAsync(NyxRelayBridgeRequest request, CancellationToken ct);
}

/// <summary>
/// Normalized relay callback inputs consumed by <see cref="INyxRelayDayOneBridge"/>.
/// </summary>
public sealed record NyxRelayBridgeRequest(
    string Text,
    string? ConversationType,
    string? ConversationId,
    string? MessageId,
    string? Platform,
    string? SenderId,
    string? SenderName,
    string ScopeId,
    string NyxIdAccessToken);
