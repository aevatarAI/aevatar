namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdChatServiceDefaults
{
    public const string ServiceId = "nyxid-chat";
    public const string DisplayName = "NyxID Chat";
    public const string GAgentKind = "nyxid.chat";
    public const string LegacyGAgentKind = "nyxid.chat.legacy";
    public const string TurnGAgentKind = "nyxid.chat.turn";
    public const string ActorIdPrefix = "nyxid-chat";
    public const string ActorsFileName = "actors";
    public const string ProviderName = "nyxid";
    public const string ModelSelfHealPublisherActorId = "nyxid-chat.model.self-heal";

    public static string GenerateActorId() =>
        $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
