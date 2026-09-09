using Aevatar.GAgents.Channel.Runtime;

namespace Aevatar.GAgents.NyxidChat;

internal static class ScheduledDeliveryMetadataBuilder
{
    internal const string NyxIdAssistantPlatform = "nyxid-chat";

    public static Dictionary<string, string> CreateNyxIdAssistantMetadata(
        string conversationId,
        string? providerSlug,
        string? providerUserServiceId)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ChannelMetadataKeys.Platform] = NyxIdAssistantPlatform,
            [ChannelMetadataKeys.ConversationId] = conversationId,
        };

        ApplyDefaultOutboundProvider(metadata, providerSlug, providerUserServiceId);
        ApplyDeliveryAddress(metadata, conversationId, NyxIdAssistantPlatform, null, null);
        return metadata;
    }

    public static void ApplyDefaultOutboundProvider(
        IDictionary<string, string> metadata,
        string? providerSlug,
        string? providerUserServiceId)
    {
        ApplyDefaultOutboundProvider(metadata, providerSlug);
        PutIfPresent(metadata, ChannelMetadataKeys.OutboundProviderUserServiceId, providerUserServiceId);
    }

    public static void ApplyDefaultOutboundProvider(
        IDictionary<string, string> metadata,
        string? providerSlug)
    {
        var normalizedProviderSlug = Normalize(providerSlug);
        if (normalizedProviderSlug is null)
            return;

        metadata[ChannelMetadataKeys.InboundChannelBotProxySlug] = normalizedProviderSlug;
        metadata[ChannelMetadataKeys.OutboundProviderSlug] = normalizedProviderSlug;
    }

    public static void ApplyDeliveryAddress(
        IDictionary<string, string> metadata,
        string? addressId,
        string? addressType,
        string? fallbackAddressId,
        string? fallbackAddressType)
    {
        PutIfPresent(metadata, ChannelMetadataKeys.DeliveryAddressId, addressId);
        PutIfPresent(metadata, ChannelMetadataKeys.DeliveryAddressType, addressType);
        PutIfPresent(metadata, ChannelMetadataKeys.DeliveryFallbackAddressId, fallbackAddressId);
        PutIfPresent(metadata, ChannelMetadataKeys.DeliveryFallbackAddressType, fallbackAddressType);
    }

    private static void PutIfPresent(
        IDictionary<string, string> metadata,
        string key,
        string? value)
    {
        var normalized = Normalize(value);
        if (normalized is not null)
            metadata[key] = normalized;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
