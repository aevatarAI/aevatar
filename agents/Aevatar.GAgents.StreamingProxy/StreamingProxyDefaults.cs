namespace Aevatar.GAgents.StreamingProxy;

public static class StreamingProxyDefaults
{
    public const string ServiceId = "streaming-proxy";
    public const string DisplayName = "Streaming Proxy";
    public static readonly string GAgentTypeName = typeof(StreamingProxyGAgent).FullName!;
    public const string ActorIdPrefix = "streaming-proxy";
    public const int MaxMessages = 500;

    public static string GenerateRoomId() =>
        $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
