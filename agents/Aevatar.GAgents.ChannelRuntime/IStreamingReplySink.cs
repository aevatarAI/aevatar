namespace Aevatar.GAgents.ChannelRuntime;

/// <summary>
/// Receives per-delta streaming updates from <see cref="IConversationReplyGenerator"/> so the reply
/// inbox can fan the accumulated text to the conversation actor as it is being generated. The
/// actor is the sole holder of the relay reply token, so only it is allowed to drive the relay
/// placeholder send and subsequent edit calls; this sink therefore fans out signals (chunk events)
/// and never touches the outbound port directly.
/// </summary>
/// <remarks>
/// Implementations are per-turn and owned by the inbox runtime. A null sink signals that streaming
/// is disabled for the turn (for example, the feature flag is off, the activity is not a relay
/// turn, or an earlier failure invalidated the turn); generators must tolerate a null sink by
/// simply accumulating the final text without calling any sink method.
/// </remarks>
internal interface IStreamingReplySink
{
    /// <summary>
    /// Reports the accumulated reply text after a new delta has arrived. The implementation
    /// decides whether to forward the update (throttle or drop) and is expected to dispatch a
    /// <c>LlmReplyStreamChunkEvent</c> to the conversation actor when it does.
    /// </summary>
    Task OnDeltaAsync(string accumulatedText, CancellationToken ct);
}
