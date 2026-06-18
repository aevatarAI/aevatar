using Aevatar.Foundation.Abstractions;

namespace Aevatar.GAgents.Scheduled;

public static class UserAgentCatalogSharingAudience
{
    public static bool TryBuildKey(OwnerScope? ownerScope, out string audienceKey)
    {
        if (ownerScope is null ||
            ownerScope.IsNyxIdNative ||
            string.IsNullOrWhiteSpace(ownerScope.Platform) ||
            string.IsNullOrWhiteSpace(ownerScope.RegistrationScopeId))
        {
            audienceKey = string.Empty;
            return false;
        }

        audienceKey = $"{ownerScope.Platform.Trim().ToLowerInvariant()}:{ownerScope.RegistrationScopeId.Trim()}";
        return true;
    }
}
