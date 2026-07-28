using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.Channel.Abstractions;

namespace Aevatar.GAgents.Channel.Identity.Abstractions;

/// <summary>Builds deterministic actor and Vault identities for a native NyxID user.</summary>
public static class ManagedCodexCredentialActorIdentity
{
    /// <summary>Actor ID prefix for managed Codex credential owners.</summary>
    public const string Prefix = "managed-codex-credential";

    /// <summary>Stable Vault subject within the unique per-user owner scope.</summary>
    public const string SecretSubjectId = "invocation-agent-key";

    /// <summary>Builds the actor/owner-scope ID from the complete NyxID authority.</summary>
    public static string From(ExternalSubjectRef owner)
    {
        ExternalSubjectRefExtensions.EnsureValid(owner);
        if (!string.Equals(owner.Platform, OwnerScope.NyxIdPlatform, StringComparison.Ordinal))
            throw new ArgumentException("Managed Codex credentials require a native NyxID owner.", nameof(owner));

        return $"{Prefix}:{owner.Platform}:{owner.Tenant ?? string.Empty}:{owner.ExternalUserId}";
    }
}
