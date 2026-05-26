namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Dispatches a channel-agnostic interactive reply intent through the configured relay transport,
/// resolving a per-channel composer and assembling the transport body.
/// </summary>
public interface IInteractiveReplyDispatcher
{
    /// <summary>
    /// Dispatches the supplied reply intent as the reply to the relay message identified by
    /// <paramref name="messageId"/>, using <paramref name="relayToken"/> for authentication and
    /// <paramref name="channel"/> to select the composer.
    /// </summary>
    Task<InteractiveReplyDispatchResult> DispatchAsync(
        ChannelId channel,
        string messageId,
        string relayToken,
        MessageContent intent,
        ComposeContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Outcome of one <see cref="IInteractiveReplyDispatcher.DispatchAsync"/> call.</summary>
/// <param name="Succeeded">Whether the transport accepted the reply.</param>
/// <param name="MessageId">Transport-side reply message identifier when successful.</param>
/// <param name="PlatformMessageId">Platform-native message identifier when successful.</param>
/// <param name="Capability">Composer capability used to render the reply.</param>
/// <param name="FellBackToText">Whether the dispatcher downgraded to plain text.</param>
/// <param name="Detail">Transport-reported error detail when unsuccessful.</param>
public sealed record InteractiveReplyDispatchResult(
    bool Succeeded,
    string? MessageId,
    string? PlatformMessageId,
    ComposeCapability Capability,
    bool FellBackToText,
    string? Detail);
