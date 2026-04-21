namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Sends, updates, deletes, and proactively continues one channel conversation.
/// </summary>
public interface IChannelOutboundPort
{
    /// <summary>
    /// Gets the channel this outbound port targets.
    /// </summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Gets the declared capability matrix for this outbound path.
    /// </summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>
    /// Sends a new outbound message using the adapter's bot credential.
    /// </summary>
    Task<EmitResult> SendAsync(ConversationReference to, MessageContent content, CancellationToken ct);

    /// <summary>
    /// Updates one previously emitted activity identified by the adapter-owned activity id.
    /// </summary>
    Task<EmitResult> UpdateAsync(
        ConversationReference to,
        string activityId,
        MessageContent content,
        CancellationToken ct);

    /// <summary>
    /// Deletes one previously emitted activity identified by the adapter-owned activity id.
    /// </summary>
    Task DeleteAsync(ConversationReference to, string activityId, CancellationToken ct);

    /// <summary>
    /// Starts or continues a conversation from outside an inbound turn using the explicit auth context supplied by the caller.
    /// </summary>
    Task<EmitResult> ContinueConversationAsync(
        ConversationReference reference,
        MessageContent content,
        AuthContext auth,
        CancellationToken ct);
}
