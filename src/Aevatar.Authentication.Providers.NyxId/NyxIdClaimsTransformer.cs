using System.Security.Claims;
using Aevatar.Authentication.Abstractions;

namespace Aevatar.Authentication.Providers.NyxId;

/// <summary>
/// Maps NyxID token claims to Aevatar standard claims.
/// Waterfall: scope_id → uid → sub → NameIdentifier.
/// </summary>
public sealed class NyxIdClaimsTransformer : IAevatarClaimsTransformer
{
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

        // Scope may only come from an explicit, known claim type. A token that carries
        // scope only in some arbitrary *_id claim intentionally maps to no scope_id (and is
        // therefore denied) rather than silently binding to an unvetted claim value.
        foreach (var claimType in ScopeClaimCandidates)
        {
            var claimValue = identity.FindFirst(claimType)?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(claimValue))
                continue;

            yield return new Claim(AevatarStandardClaimTypes.ScopeId, claimValue);
            yield break;
        }
    }
}
