namespace Aevatar.GAgents.NyxidChat.Voice;

public static class NyxIdVoiceServiceDefaults
{
    public const string GAgentKind = "nyxid.voice";
    public const string DisplayName = "NyxID Voice";
    public const string ActorIdPrefix = "nyxid-voice";
    public const string OpenAIRealtimeModuleName = "voice_presence_openai";

    public static string GenerateActorId() =>
        $"{ActorIdPrefix}-{Guid.NewGuid():N}";
}
