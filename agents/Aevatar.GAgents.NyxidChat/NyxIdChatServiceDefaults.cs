namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdChatServiceDefaults
{
    public const string ServiceId = "nyxid-chat";
    public const string DisplayName = "NyxID Chat";
    public const string GAgentTypeName = "Aevatar.GAgents.NyxidChat.NyxIdChatGAgent";
    public const string ActorIdPrefix = "nyxid-chat";
    public const string ActorsFileName = "actors";
    public const string ProviderName = "nyxid";

    public static string GenerateActorId() =>
        $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
