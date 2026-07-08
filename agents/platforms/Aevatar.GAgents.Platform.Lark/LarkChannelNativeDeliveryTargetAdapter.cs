using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkChannelNativeDeliveryTargetAdapter : IChannelNativeDeliveryTargetAdapter
{
    public ChannelId Channel => ChannelId.From("lark");

    public ChannelNativeDeliveryTarget Adapt(ChannelNativeDeliveryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new LarkChannelNativeDeliveryTarget(
            target.AgentId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            target.NyxApiKey,
            ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveId)),
            ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveIdType)),
            ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveIdFallback)),
            ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveIdTypeFallback)));
    }

    private static string ReadStringProperty(ChannelNativeDeliveryTarget target, string propertyName) =>
        target.GetType().GetProperty(propertyName)?.GetValue(target) as string ?? string.Empty;

    private sealed record LarkChannelNativeDeliveryTarget(
        string AgentId,
        string Platform,
        string ConversationId,
        string NyxProviderSlug,
        string NyxApiKey,
        string LarkReceiveId,
        string LarkReceiveIdType,
        string LarkReceiveIdFallback,
        string LarkReceiveIdTypeFallback)
        : ChannelNativeDeliveryTarget(
            AgentId,
            Platform,
            ConversationId,
            NyxProviderSlug,
            NyxApiKey),
            ILarkChannelNativeDeliveryRoute;
}
