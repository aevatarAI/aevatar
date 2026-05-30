namespace Aevatar.GAgents.NyxidChat;

using System.Security.Cryptography;
using System.Text;

public static class NyxIdChatServiceDefaults
{
    public const string ServiceId = "nyxid-chat";
    public const string DisplayName = "NyxID Chat";
    public static readonly string GAgentTypeName = typeof(NyxIdChatGAgent).FullName!;
    public const string ActorIdPrefix = "nyxid-chat";
    public const string ActorsFileName = "actors";
    public const string ProviderName = "nyxid";
    public const string ModelSelfHealPublisherActorId = "nyxid-chat.model.self-heal";

    public static string GenerateActorId() =>
        $"{ActorIdPrefix}-{Guid.NewGuid():N}";

    public static string BuildVoiceDemoActorId(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim()));
        var hash = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        return $"{ActorIdPrefix}-voice-demo-{hash}";
    }
}
