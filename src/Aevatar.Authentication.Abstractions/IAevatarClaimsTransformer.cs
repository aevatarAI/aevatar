using System.Security.Claims;

namespace Aevatar.Authentication.Abstractions;

/// <summary>
/// Maps provider-specific claims to Aevatar standard claims.
/// Each authentication provider implements this to normalize its token claims
/// into a common format that GAgentService scope gating can consume.
/// </summary>
public interface IAevatarClaimsTransformer
{
    /// <summary>
    /// Returns additional claims to add to the principal.
    /// Implementations should map provider-specific claim types
    /// (e.g. NyxID's "uid") to <see cref="AevatarStandardClaimTypes"/>.
    /// </summary>
    IEnumerable<Claim> TransformClaims(ClaimsPrincipal principal);
}
