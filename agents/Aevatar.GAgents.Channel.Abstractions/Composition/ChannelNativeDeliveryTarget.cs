namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Credential-bearing delivery target consumed by channel-native outbound senders.
/// </summary>
/// <remarks>
/// This DTO carries only channel-neutral identity and credential material. Platform-specific
/// routing shape belongs to the platform sender boundary.
/// </remarks>
public record ChannelNativeDeliveryTarget(
    string AgentId,
    string Platform,
    string ConversationId,
    string NyxProviderSlug,
    string NyxApiKey);
