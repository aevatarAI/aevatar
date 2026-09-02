namespace Aevatar.GAgents.Channel.Abstractions;

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
    Task<EmitResult> SendAsync(
        ChannelNativeDeliveryTarget target,
        ChannelNativeMessage message,
        CancellationToken cancellationToken);

    /// <summary>Updates a previously sent native message identified by its platform message id.</summary>
    Task<EmitResult> UpdateAsync(
        ChannelNativeDeliveryTarget target,
        string platformMessageId,
        ChannelNativeMessage message,
        bool isFinal,
        CancellationToken cancellationToken);
}
