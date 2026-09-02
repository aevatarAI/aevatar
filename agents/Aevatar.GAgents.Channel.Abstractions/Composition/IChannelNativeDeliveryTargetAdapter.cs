namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Platform-owned adapter from the internal scheduled delivery target into the channel-native sender target.
/// </summary>
public interface IChannelNativeDeliveryTargetAdapter
{
    /// <summary>
    /// Platform handled by this adapter.
    /// </summary>
    ChannelId Channel { get; }

    /// <summary>
    /// Converts a neutral delivery target into the platform-specific native sender shape.
    /// </summary>
    ChannelNativeDeliveryTarget Adapt(ChannelNativeDeliveryTarget target);
}
