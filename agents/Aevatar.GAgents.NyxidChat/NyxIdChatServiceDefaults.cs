using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgents.NyxidChat;

public static class NyxIdChatServiceDefaults
{
    public const string ServiceId = "nyxid-chat";
    public const string DisplayName = "NyxID Chat";
    public static readonly string GAgentTypeName = typeof(NyxIdChatGAgent).FullName!;
    public const string ActorIdPrefix = "nyxid-chat";
    public const string ActorsFileName = "actors";
    public const string ProviderName = "nyxid";

    public static string GenerateActorId(string scopeId) =>
        $"{ActorIdPrefix}-{Guid.NewGuid():N}:scope:{ComputeScopeHash(scopeId)}";

    public static bool IsActorIdForScope(string? actorId, string? scopeId)
    {
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(scopeId))
            return false;

        var suffix = $":scope:{ComputeScopeHash(scopeId)}";
        return actorId.Trim().StartsWith($"{ActorIdPrefix}-", StringComparison.Ordinal) &&
               actorId.Trim().EndsWith(suffix, StringComparison.Ordinal);
    }

    private static string ComputeScopeHash(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopeId.Trim())))
            .ToLowerInvariant();
    }
}
