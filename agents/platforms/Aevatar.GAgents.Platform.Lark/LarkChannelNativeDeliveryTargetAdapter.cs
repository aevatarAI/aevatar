using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkChannelNativeDeliveryTargetAdapter : IChannelNativeDeliveryTargetAdapter
{
    public ChannelId Channel => ChannelId.From("lark");

    public ChannelNativeDeliveryTarget Adapt(ChannelNativeDeliveryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var address = (target as IChannelDeliveryAddressTarget)?.ChannelAddress;
        var route = LarkReceiveTargetRoute.From(target);

        return new LarkChannelNativeDeliveryTarget(
            target.AgentId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            target.NyxApiKey,
            FirstNonWhiteSpace(address?.Primary.AddressId, route.LarkReceiveId),
            FirstNonWhiteSpace(address?.Primary.AddressType, route.LarkReceiveIdType),
            FirstNonWhiteSpace(address?.Fallback?.AddressId, route.LarkReceiveIdFallback),
            FirstNonWhiteSpace(address?.Fallback?.AddressType, route.LarkReceiveIdTypeFallback));
    }

    private static string FirstNonWhiteSpace(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return string.Empty;
    }

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

    private sealed record LarkReceiveTargetRoute(
        string LarkReceiveId,
        string LarkReceiveIdType,
        string LarkReceiveIdFallback,
        string LarkReceiveIdTypeFallback)
    {
        public static LarkReceiveTargetRoute From(ChannelNativeDeliveryTarget target)
        {
            if (target is ILarkChannelNativeDeliveryRoute route)
            {
                return new LarkReceiveTargetRoute(
                    route.LarkReceiveId,
                    route.LarkReceiveIdType,
                    route.LarkReceiveIdFallback,
                    route.LarkReceiveIdTypeFallback);
            }

            return new LarkReceiveTargetRoute(
                ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveId)),
                ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveIdType)),
                ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveIdFallback)),
                ReadStringProperty(target, nameof(ILarkChannelNativeDeliveryRoute.LarkReceiveIdTypeFallback)));
        }

        private static string ReadStringProperty(ChannelNativeDeliveryTarget target, string propertyName) =>
            target.GetType().GetProperty(propertyName)?.GetValue(target) as string ?? string.Empty;
    }
}
