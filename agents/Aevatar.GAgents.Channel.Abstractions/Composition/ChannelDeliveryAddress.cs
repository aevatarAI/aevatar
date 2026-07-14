namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Generic outbound channel address captured by catalog-backed delivery targets.
/// </summary>
public sealed record ChannelDeliveryAddress(
    string Platform,
    string ProviderSlug,
    string ConversationId,
    ChannelDeliveryAddressEndpoint Primary,
    ChannelDeliveryAddressEndpoint? Fallback = null)
{
    /// <summary>
    /// Empty channel address.
    /// </summary>
    public static ChannelDeliveryAddress Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        ChannelDeliveryAddressEndpoint.Empty);
}

/// <summary>
/// Provider-interpreted channel address endpoint.
/// </summary>
public sealed record ChannelDeliveryAddressEndpoint(
    string AddressId,
    string AddressType)
{
    /// <summary>
    /// Empty channel address endpoint.
    /// </summary>
    public static ChannelDeliveryAddressEndpoint Empty { get; } = new(string.Empty, string.Empty);
}

/// <summary>
/// Delivery target that carries a typed generic channel address.
/// </summary>
public interface IChannelDeliveryAddressTarget
{
    /// <summary>
    /// Generic address to deliver to on the target channel.
    /// </summary>
    ChannelDeliveryAddress ChannelAddress { get; }
}
