namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// Lark-owned extension surface for channel-native delivery targets that carry a typed
/// receive target and optional fallback.
/// </summary>
public interface ILarkChannelNativeDeliveryRoute
{
    string LarkReceiveId { get; }

    string LarkReceiveIdType { get; }

    string LarkReceiveIdFallback { get; }

    string LarkReceiveIdTypeFallback { get; }
}
