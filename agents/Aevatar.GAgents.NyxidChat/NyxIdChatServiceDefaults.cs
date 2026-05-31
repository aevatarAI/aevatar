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

    // Refactor (iter367/cluster-issue674): Old pattern: voice demo actor ids were
    // derived ad hoc by bootstrap callers. New principle: the NyxID chat boundary
    // owns deterministic voice demo actor identity, while typed ChatRouteVoiceAttachTarget
    // carries that actor id to voice routing.
    public static string BuildVoiceDemoActorId(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim()));
        var hash = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        return $"{ActorIdPrefix}-voice-demo-{hash}";
    }
}
