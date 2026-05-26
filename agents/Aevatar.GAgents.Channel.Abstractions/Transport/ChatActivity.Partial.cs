namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Deterministic helpers for <see cref="ChatActivity"/> identity.
/// </summary>
public sealed partial class ChatActivity
{
    /// <summary>
    /// Builds one deterministic activity id from the channel identifier and the adapter-owned
    /// platform delivery key so redelivery of the same inbound event yields the same id.
    /// </summary>
    /// <param name="channel">The channel whose delivery key scopes the id.</param>
    /// <param name="deliveryKey">The stable platform delivery key assigned to the inbound event.</param>
    /// <returns>The deterministic activity id in the form <c>channel:delivery_key</c>.</returns>
    [ActivityIdGenerator]
    public static string BuildActivityId(ChannelId channel, string deliveryKey)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (string.IsNullOrWhiteSpace(channel.Value))
        {
            throw new ArgumentException("Channel value cannot be empty.", nameof(channel));
        }

        if (string.IsNullOrWhiteSpace(deliveryKey))
        {
            throw new ArgumentException("Delivery key cannot be empty.", nameof(deliveryKey));
        }

        return string.Concat(channel.Value.Trim(), ":", deliveryKey.Trim());
    }
}
