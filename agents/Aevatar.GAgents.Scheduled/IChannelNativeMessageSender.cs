using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Scheduled;

/// <summary>
/// Sends a produced <see cref="ChannelNativeMessage"/> through one channel's native transport.
/// </summary>
/// <remarks>
/// Delivery adapters implement this interface so channel-neutral orchestrators can choose a
/// sender by <see cref="Channel"/> without embedding platform-specific request construction
/// or failure parsing.
/// </remarks>
public interface IChannelNativeMessageSender
{
    /// <summary>Gets the channel this sender targets.</summary>
    ChannelId Channel { get; }

    /// <summary>Sends the native message to the resolved delivery target.</summary>
    Task SendAsync(
        UserAgentDeliveryTarget target,
        ChannelNativeMessage message,
        CancellationToken cancellationToken);
}
