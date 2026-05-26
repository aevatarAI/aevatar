namespace Aevatar.GAgents.StreamingProxy;

public static class StreamingProxyDefaults
{
    public const string ServiceId = "streaming-proxy";
    public const string DisplayName = "Streaming Proxy";
    public static readonly string GAgentTypeName = typeof(StreamingProxyGAgent).FullName!;
    public const string ActorIdPrefix = "streaming-proxy";
    public const int MaxMessages = 500;
    public const int MaxDiscussionRounds = 4;
    public const int MaxTranscriptMessagesPerPrompt = 12;
    public const int InitialResponseTimeoutMs = 15000;
    public const int PostTopicTimeoutMs = 5000;
    public const int IdleCompletionTimeoutMs = 1500;
    public const int TerminalCompletionTimeoutMs = 5000;

    public static string GenerateRoomId() =>
        $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
