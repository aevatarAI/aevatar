using System.Security.Claims;
using Aevatar.Authentication.Abstractions;

namespace Aevatar.Authentication.Providers.NyxId;

/// <summary>
/// Maps NyxID token claims to Aevatar standard claims.
/// Waterfall: scope_id → uid → sub → NameIdentifier → any *_id claim.
/// </summary>
public sealed class NyxIdClaimsTransformer : IAevatarClaimsTransformer
{
    private static readonly HashSet<string> IgnoredGenericIdClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "client_id",
        "session_id",
        "sid",
    };

    private static readonly string[] ScopeClaimCandidates =
    [
        AevatarStandardClaimTypes.ScopeId,
        "uid",
        "sub",
        ClaimTypes.NameIdentifier,
    ];

    public IEnumerable<Claim> TransformClaims(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity)
            yield break;

        // Already has scope_id — no mapping needed
        if (identity.FindFirst(AevatarStandardClaimTypes.ScopeId) != null)
            yield break;

        // Try known claim types in priority order
        foreach (var claimType in ScopeClaimCandidates)
        {
            var claimValue = identity.FindFirst(claimType)?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(claimValue))
                continue;

            yield return new Claim(AevatarStandardClaimTypes.ScopeId, claimValue);
            yield break;
        }

        // Fallback: any *_id claim not in the ignore list
        var genericIdClaim = identity.Claims.FirstOrDefault(claim =>
            claim.Type.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
            !IgnoredGenericIdClaimTypes.Contains(claim.Type) &&
            !string.IsNullOrWhiteSpace(claim.Value));

        if (genericIdClaim != null)
            yield return new Claim(AevatarStandardClaimTypes.ScopeId, genericIdClaim.Value.Trim());
    }
}
