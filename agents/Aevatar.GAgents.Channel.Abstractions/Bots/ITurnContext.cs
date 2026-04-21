namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Exposes one inbound activity together with the constrained outbound operations allowed during the turn.
/// </summary>
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
    Task<EmitResult> SendAsync(MessageContent content);

    /// <summary>
    /// Sends a reply associated with the current inbound activity.
    /// </summary>
    Task<EmitResult> ReplyAsync(MessageContent content);

    /// <summary>
    /// Starts a streaming reply whose concrete lifecycle is implemented by the active adapter.
    /// </summary>
    Task<StreamingHandle> BeginStreamingReplyAsync(MessageContent initial);

    /// <summary>
    /// Updates one activity previously emitted during this turn.
    /// </summary>
    Task<EmitResult> UpdateAsync(string activityId, MessageContent content);

    /// <summary>
    /// Deletes one activity previously emitted during this turn.
    /// </summary>
    Task<EmitResult> DeleteAsync(string activityId);
}
