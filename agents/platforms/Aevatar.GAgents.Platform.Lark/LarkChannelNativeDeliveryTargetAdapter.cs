using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Platform.Lark;

public sealed class LarkChannelNativeDeliveryTargetAdapter : IChannelNativeDeliveryTargetAdapter
{
    public ChannelId Channel => ChannelId.From("lark");

    public ChannelNativeDeliveryTarget Adapt(ChannelNativeDeliveryTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var address = (target as IChannelDeliveryAddressTarget)?.ChannelAddress;
        var route = target as ILarkChannelNativeDeliveryRoute;

        return new LarkChannelNativeDeliveryTarget(
            target.AgentId,
            target.Platform,
            target.ConversationId,
            target.NyxProviderSlug,
            target.NyxApiKey,
            FirstNonWhiteSpace(address?.Primary.AddressId, route?.LarkReceiveId),
            FirstNonWhiteSpace(address?.Primary.AddressType, route?.LarkReceiveIdType),
            FirstNonWhiteSpace(address?.Fallback?.AddressId, route?.LarkReceiveIdFallback),
            FirstNonWhiteSpace(address?.Fallback?.AddressType, route?.LarkReceiveIdTypeFallback));
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
}
