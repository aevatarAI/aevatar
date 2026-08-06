using System.Security.Cryptography;
using System.Text;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Abstractions.AgentProfiles;

namespace Aevatar.GAgentService.Core.AgentProfiles;

public static class AgentProfileActorIds
{
    public static string Namespace(AgentProfileOwner owner) =>
        $"agent-profile-namespace-{Digest(OwnerKey(owner))}";

    public static string Profile(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        return $"agent-profile-{Digest(profileId.Trim())}";
    }

    private static string OwnerKey(AgentProfileOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return owner.OwnerCase switch
        {
            AgentProfileOwner.OwnerOneofCase.Scope when !string.IsNullOrWhiteSpace(owner.Scope.ScopeId) =>
                $"scope:{owner.Scope.ScopeId.Trim()}",
            AgentProfileOwner.OwnerOneofCase.System when
                string.Equals(owner.System.PlatformId, AgentProfileOwners.PlatformId, StringComparison.Ordinal) =>
                $"system:{AgentProfileOwners.PlatformId}",
            _ => throw new ArgumentException("A valid Agent Profile owner is required.", nameof(owner)),
        };
    }

    private static string Digest(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }
}
