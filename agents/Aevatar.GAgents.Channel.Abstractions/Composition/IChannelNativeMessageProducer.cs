namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Produces a <see cref="ChannelNativeMessage"/> for one channel from a channel-agnostic intent.
/// </summary>
/// <remarks>
/// Adapter authors implement this interface alongside (or wrapping) <see cref="IMessageComposer"/> so that
/// cross-platform dispatchers can consume a unified DTO without depending on adapter-specific native shapes.
/// </remarks>
public interface IChannelNativeMessageProducer
{
    /// <summary>Gets the channel this producer targets.</summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Evaluates whether the supplied intent can be expressed exactly, degraded, or not at all by this channel.
    /// </summary>
    ComposeCapability Evaluate(MessageContent intent, ComposeContext context);

    /// <summary>
    /// Produces the platform-neutral native payload for the supplied intent.
    /// </summary>
    ChannelNativeMessage Produce(MessageContent intent, ComposeContext context);
}
