namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Credential-bearing delivery target consumed by channel-native outbound senders.
/// </summary>
/// <remarks>
/// This DTO carries only native transport routing and credential material. Catalog,
/// workflow, or scheduled-run ownership stays outside the sender abstraction.
/// </remarks>
public sealed record ChannelNativeDeliveryTarget(
    string AgentId,
    string Platform,
    string ConversationId,
    string NyxProviderSlug,
    string NyxApiKey,
    string LarkReceiveId,
    string LarkReceiveIdType,
    string LarkReceiveIdFallback,
    string LarkReceiveIdTypeFallback);
