using System.Security.Cryptography;
using System.Text;

namespace Aevatar.GAgentService.Core.AgentProfiles;

public static class AgentProfileActorIds
{
    public const string Namespace = "gagent-service:agent-profile-namespace:v1";

    public static string Profile(string profileId) =>
        $"gagent-service:agent-profile:{HashOpaqueAddress(profileId)}";

    private static string HashOpaqueAddress(string profileId) =>
        Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes(
                    $"aevatar.agent-profile.actor-address.v1\n{NormalizeRequired(profileId)}"))
            .AsSpan(0, 18));

    private static string NormalizeRequired(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException("Profile id cannot have boundary whitespace.", nameof(value));
        return value;
    }
}
