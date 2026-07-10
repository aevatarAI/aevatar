using ChannelAddressModel = Aevatar.GAgents.Channel.Abstractions.ChannelDeliveryAddress;
using ChannelAddressEndpointModel = Aevatar.GAgents.Channel.Abstractions.ChannelDeliveryAddressEndpoint;

namespace Aevatar.GAgents.Scheduled;

public static class UserAgentCatalogChannelAddress
{
    public static ChannelDeliveryAddress FromParts(
        string? platform,
        string? providerSlug,
        string? conversationId,
        string? addressId,
        string? addressType,
        string? fallbackAddressId,
        string? fallbackAddressType)
    {
        var normalizedConversationId = Normalize(conversationId) ?? string.Empty;
        var primaryAddressId = Normalize(addressId) ?? normalizedConversationId;
        var primaryAddressType = Normalize(addressType) ?? string.Empty;
        var fallbackId = Normalize(fallbackAddressId);
        var fallbackType = Normalize(fallbackAddressType);

        return new ChannelDeliveryAddress
        {
            Platform = Normalize(platform) ?? string.Empty,
            ProviderSlug = Normalize(providerSlug) ?? string.Empty,
            ConversationId = normalizedConversationId,
            Primary = new ChannelDeliveryAddressEndpoint
            {
                AddressId = primaryAddressId,
                AddressType = primaryAddressType,
            },
            Fallback = string.IsNullOrWhiteSpace(fallbackId) && string.IsNullOrWhiteSpace(fallbackType)
                ? null
                : new ChannelDeliveryAddressEndpoint
                {
                    AddressId = fallbackId ?? string.Empty,
                    AddressType = fallbackType ?? string.Empty,
                },
        };
    }

    public static ChannelDeliveryAddress Merge(
        ChannelDeliveryAddress? incoming,
        ChannelDeliveryAddress? existing,
        string? platform,
        string? providerSlug,
        string? conversationId)
    {
        var merged = incoming?.Clone() ?? existing?.Clone() ?? new ChannelDeliveryAddress();
        merged.Platform = MergeNonEmpty(merged.Platform, platform);
        merged.ProviderSlug = MergeNonEmpty(merged.ProviderSlug, providerSlug);
        merged.ConversationId = MergeNonEmpty(merged.ConversationId, conversationId);

        var primary = merged.Primary ?? new ChannelDeliveryAddressEndpoint();
        primary.AddressId = MergeNonEmpty(primary.AddressId, merged.ConversationId);
        merged.Primary = primary;
        return merged;
    }

    public static ChannelAddressModel ToModel(
        ChannelDeliveryAddress? address,
        string? platform,
        string? providerSlug,
        string? conversationId)
    {
        var resolvedPlatform = Normalize(address?.Platform) ?? Normalize(platform) ?? string.Empty;
        var resolvedProviderSlug = Normalize(address?.ProviderSlug) ?? Normalize(providerSlug) ?? string.Empty;
        var resolvedConversationId = Normalize(address?.ConversationId) ?? Normalize(conversationId) ?? string.Empty;
        var primary = address?.Primary;
        var primaryAddressId = Normalize(primary?.AddressId) ?? resolvedConversationId;
        var primaryAddressType = Normalize(primary?.AddressType) ?? string.Empty;
        var fallback = address?.Fallback;
        var fallbackAddressId = Normalize(fallback?.AddressId);
        var fallbackAddressType = Normalize(fallback?.AddressType);

        return new ChannelAddressModel(
            resolvedPlatform,
            resolvedProviderSlug,
            resolvedConversationId,
            new ChannelAddressEndpointModel(primaryAddressId, primaryAddressType),
            string.IsNullOrWhiteSpace(fallbackAddressId) && string.IsNullOrWhiteSpace(fallbackAddressType)
                ? null
                : new ChannelAddressEndpointModel(fallbackAddressId ?? string.Empty, fallbackAddressType ?? string.Empty));
    }

    private static string MergeNonEmpty(string? preferred, string? fallback) =>
        Normalize(preferred) ?? Normalize(fallback) ?? string.Empty;

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
