namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Exposes one inbound activity together with the constrained outbound operations allowed during the turn.
/// </summary>
/// <remarks>
/// This contract intentionally hides <see cref="IChannelTransport"/> and <see cref="IChannelOutboundPort"/> so bot code
/// cannot bypass turn-scoped auth, targeting, or ordering rules. Outbound helpers automatically target
/// <see cref="Activity"/>'s conversation and use the bot credential bound to the active adapter instance.
/// </remarks>
public interface ITurnContext
{
    /// <summary>
    /// Gets the normalized inbound activity that triggered this turn.
    /// </summary>
    ChatActivity Activity { get; }

    /// <summary>
    /// Gets the bot descriptor bound to the current turn without exposing transport credentials.
    /// </summary>
    ChannelBotDescriptor Bot { get; }

    /// <summary>
    /// Gets the service provider that owns the turn lifetime.
    /// </summary>
    IServiceProvider Services { get; }

    /// <summary>
    /// Sends a new outbound activity in the current conversation.
    /// </summary>
    /// <param name="content">The channel-agnostic outbound message intent.</param>
    /// <returns>The emit result produced by the active adapter.</returns>
    /// <remarks>
    /// This helper always targets <see cref="Activity"/>'s conversation and uses the adapter's bot credential for the
    /// current turn.
    /// </remarks>
    Task<EmitResult> SendAsync(MessageContent content);

    /// <summary>
    /// Sends a reply associated with the current inbound activity.
    /// </summary>
    /// <param name="content">The channel-agnostic reply intent.</param>
    /// <returns>The emit result produced by the active adapter.</returns>
    /// <remarks>
    /// Implementations should preserve reply linkage with the triggering inbound activity where the target channel supports it.
    /// </remarks>
    Task<EmitResult> ReplyAsync(MessageContent content);

    /// <summary>
    /// Starts a streaming reply whose concrete lifecycle is implemented by the active adapter.
    /// </summary>
    /// <param name="initial">The initial message content used to seed the streaming reply.</param>
    /// <returns>The adapter-owned streaming handle for the in-flight reply.</returns>
    /// <remarks>
    /// The returned handle encapsulates debounce, chunk ordering, and interruption semantics so bot code does not need
    /// channel-specific streaming logic.
    /// </remarks>
    Task<StreamingHandle> BeginStreamingReplyAsync(MessageContent initial);

    /// <summary>
    /// Updates one activity previously emitted during this turn.
    /// </summary>
    /// <param name="activityId">The opaque activity identifier returned by a previous send or reply.</param>
    /// <param name="content">The replacement content.</param>
    /// <returns>The emit result produced by the active adapter.</returns>
    Task<EmitResult> UpdateAsync(string activityId, MessageContent content);

    /// <summary>
    /// Deletes one activity previously emitted during this turn.
    /// </summary>
    /// <param name="activityId">The opaque activity identifier returned by a previous send or reply.</param>
    /// <returns>The emit result produced by the active adapter.</returns>
    Task<EmitResult> DeleteAsync(string activityId);
}
