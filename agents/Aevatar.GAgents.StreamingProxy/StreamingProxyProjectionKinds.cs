namespace Aevatar.GAgents.StreamingProxy;

internal static class StreamingProxyProjectionKinds
{
    public const string RoomChatSession = "streaming-proxy-room-chat-session";
    public const string RoomSubscriptionSession = "streaming-proxy-room-subscription-session";
    public const string RoomSession = RoomChatSession;
    public const string CurrentState = "streaming-proxy-current-state";
}
