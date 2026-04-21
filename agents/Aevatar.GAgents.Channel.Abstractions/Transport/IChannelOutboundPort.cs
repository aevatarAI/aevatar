namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Sends, updates, deletes, and proactively continues channel conversations through the dispatch-facing side of one adapter.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to share lifecycle and credential state with the same concrete object's
/// <see cref="IChannelTransport"/> implementation.
/// </para>
/// <para>
/// <see cref="SendAsync"/>, <see cref="UpdateAsync"/>, and <see cref="DeleteAsync"/> use the adapter's initialized bot
/// credential by default. <see cref="ContinueConversationAsync"/> requires an explicit <see cref="AuthContext"/> because no
/// inbound turn exists to infer the principal.
/// </para>
/// </remarks>
public interface IChannelOutboundPort
{
    /// <summary>
    /// Gets the stable channel identifier targeted by this adapter instance.
    /// </summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Gets the declared capability matrix shared with the transport-side contract implemented by the same adapter instance.
    /// </summary>
    ChannelCapabilities Capabilities { get; }

    /// <summary>
    /// Sends a new outbound message using the adapter's initialized bot credential.
    /// </summary>
    /// <param name="to">The normalized target conversation.</param>
    /// <param name="content">The channel-agnostic outbound message intent.</param>
    /// <param name="ct">A token that cancels the send.</param>
    /// <returns>The emit result, including the adapter-owned activity identifier on success.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the adapter has not completed initialization and receive startup.
    /// </exception>
    /// <remarks>
    /// This method is the narrow outbound primitive used by turn-scoped helpers. Callers must treat the returned
    /// <see cref="EmitResult.SentActivityId"/> as an opaque token that is only meaningful to the same adapter.
    /// </remarks>
    Task<EmitResult> SendAsync(ConversationReference to, MessageContent content, CancellationToken ct);

    /// <summary>
    /// Updates one previously emitted activity identified by the adapter-owned activity id.
    /// </summary>
    /// <param name="to">The normalized target conversation.</param>
    /// <param name="activityId">The opaque activity identifier previously returned by this adapter.</param>
    /// <param name="content">The replacement message content.</param>
    /// <param name="ct">A token that cancels the update.</param>
    /// <returns>The emit result for the update attempt.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the adapter has not completed initialization and receive startup.
    /// </exception>
    /// <remarks>
    /// The supplied <paramref name="activityId"/> must be echoed back to the same adapter implementation that produced it.
    /// Business code must not parse or reconstruct it.
    /// </remarks>
    Task<EmitResult> UpdateAsync(
        ConversationReference to,
        string activityId,
        MessageContent content,
        CancellationToken ct);

    /// <summary>
    /// Deletes one previously emitted activity identified by the adapter-owned activity id.
    /// </summary>
    /// <param name="to">The normalized target conversation.</param>
    /// <param name="activityId">The opaque activity identifier previously returned by this adapter.</param>
    /// <param name="ct">A token that cancels the delete operation.</param>
    /// <returns>A task that completes when the delete attempt has finished.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the adapter has not completed initialization and receive startup.
    /// </exception>
    Task DeleteAsync(ConversationReference to, string activityId, CancellationToken ct);

    /// <summary>
    /// Starts or continues a conversation from outside an inbound turn using the explicit auth context supplied by the caller.
    /// </summary>
    /// <param name="reference">The normalized target conversation or conversation seed.</param>
    /// <param name="content">The outbound message intent.</param>
    /// <param name="auth">The explicit bot or delegated-user principal chosen by the caller.</param>
    /// <param name="ct">A token that cancels the proactive send.</param>
    /// <returns>The emit result for the proactive send attempt.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the adapter has not completed initialization and receive startup.
    /// </exception>
    /// <remarks>
    /// This method is reserved for proactive paths that do not have an inbound turn. The caller must decide whether the
    /// send occurs as the bot or on behalf of a user; implementations must not infer that identity implicitly.
    /// </remarks>
    Task<EmitResult> ContinueConversationAsync(
        ConversationReference reference,
        MessageContent content,
        AuthContext auth,
        CancellationToken ct);
}
