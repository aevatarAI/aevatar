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
        string? conversationId,
        string? incomingAddressId = null,
        string? incomingAddressType = null,
        string? incomingFallbackAddressId = null,
        string? incomingFallbackAddressType = null,
        string? existingAddressId = null,
        string? existingAddressType = null,
        string? existingFallbackAddressId = null,
        string? existingFallbackAddressType = null)
    {
        var merged = new ChannelDeliveryAddress
        {
            Platform = MergeNonEmpty(incoming?.Platform, platform, existing?.Platform),
            ProviderSlug = MergeNonEmpty(incoming?.ProviderSlug, providerSlug, existing?.ProviderSlug),
            ConversationId = MergeNonEmpty(incoming?.ConversationId, conversationId, existing?.ConversationId),
        };

        merged.Primary = new ChannelDeliveryAddressEndpoint
        {
            AddressId = MergeNonEmpty(
                incoming?.Primary?.AddressId,
                incomingAddressId,
                existing?.Primary?.AddressId,
                existingAddressId,
                merged.ConversationId),
            AddressType = MergeNonEmpty(
                incoming?.Primary?.AddressType,
                incomingAddressType,
                existing?.Primary?.AddressType,
                existingAddressType),
        };

        var fallbackId = MergeNonEmpty(
            incoming?.Fallback?.AddressId,
            incomingFallbackAddressId,
            existing?.Fallback?.AddressId,
            existingFallbackAddressId);
        var fallbackType = MergeNonEmpty(
            incoming?.Fallback?.AddressType,
            incomingFallbackAddressType,
            existing?.Fallback?.AddressType,
            existingFallbackAddressType);
        if (!string.IsNullOrWhiteSpace(fallbackId) || !string.IsNullOrWhiteSpace(fallbackType))
        {
            merged.Fallback = new ChannelDeliveryAddressEndpoint
            {
                AddressId = fallbackId,
                AddressType = fallbackType,
            };
        }

        return merged;
    }

    public static ChannelAddressModel ToModel(
        ChannelDeliveryAddress? address,
        string? platform,
        string? providerSlug,
        string? conversationId,
        string? legacyAddressId = null,
        string? legacyAddressType = null,
        string? legacyFallbackAddressId = null,
        string? legacyFallbackAddressType = null)
    {
        var resolvedPlatform = Normalize(address?.Platform) ?? Normalize(platform) ?? string.Empty;
        var resolvedProviderSlug = Normalize(address?.ProviderSlug) ?? Normalize(providerSlug) ?? string.Empty;
        var resolvedConversationId = Normalize(address?.ConversationId) ?? Normalize(conversationId) ?? string.Empty;
        var primary = address?.Primary;
        var primaryAddressId = Normalize(primary?.AddressId) ?? Normalize(legacyAddressId) ?? resolvedConversationId;
        var primaryAddressType = Normalize(primary?.AddressType) ?? Normalize(legacyAddressType) ?? string.Empty;
        var fallback = address?.Fallback;
        var fallbackAddressId = Normalize(fallback?.AddressId) ?? Normalize(legacyFallbackAddressId);
        var fallbackAddressType = Normalize(fallback?.AddressType) ?? Normalize(legacyFallbackAddressType);

        return new ChannelAddressModel(
            resolvedPlatform,
            resolvedProviderSlug,
            resolvedConversationId,
            new ChannelAddressEndpointModel(primaryAddressId, primaryAddressType),
            string.IsNullOrWhiteSpace(fallbackAddressId) && string.IsNullOrWhiteSpace(fallbackAddressType)
                ? null
                : new ChannelAddressEndpointModel(fallbackAddressId ?? string.Empty, fallbackAddressType ?? string.Empty));
    }

    public static ChannelDeliveryAddress ToProto(
        ChannelDeliveryAddress? address,
        string? platform,
        string? providerSlug,
        string? conversationId,
        string? legacyAddressId = null,
        string? legacyAddressType = null,
        string? legacyFallbackAddressId = null,
        string? legacyFallbackAddressType = null)
    {
        var model = ToModel(
            address,
            platform,
            providerSlug,
            conversationId,
            legacyAddressId,
            legacyAddressType,
            legacyFallbackAddressId,
            legacyFallbackAddressType);

        return FromParts(
            model.Platform,
            model.ProviderSlug,
            model.ConversationId,
            model.Primary.AddressId,
            model.Primary.AddressType,
            model.Fallback?.AddressId,
            model.Fallback?.AddressType);
    }

    private static string MergeNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = Normalize(value);
            if (normalized is not null)
                return normalized;
        }

        return string.Empty;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
