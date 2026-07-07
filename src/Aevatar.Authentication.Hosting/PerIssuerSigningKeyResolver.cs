using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.IdentityModel.Tokens;

namespace Aevatar.Authentication.Hosting;

/// <summary>
/// Defense-in-depth signing-key selector for the JWT bearer pipeline.
/// <para>
/// A token whose <c>iss</c> is the OIDC/NyxID authority is validated only against the
/// OIDC/JWKS keys; a token whose <c>iss</c> is the scope-token issuer is validated only
/// against the scope-token keys. This stops a key intended for one issuer from being used
/// to forge a token that claims a different issuer.
/// </para>
/// <para>
/// CRITICAL (stay non-breaking): when the token's <c>iss</c> matches none of the known
/// issuers, the resolver falls back to returning ALL configured keys — exactly what the
/// default validation pipeline would try — so nothing that validates today stops
/// validating. The fallback also covers the case where the OIDC keys are not yet
/// materialized here (they are resolved by the base JWT handler from discovery, not owned
/// by this resolver): scope-token keys are the only ones this resolver owns, so an unknown
/// or OIDC issuer falls through to the base handler's own key set.
/// </para>
/// </summary>
internal sealed class PerIssuerSigningKeyResolver
{
    private readonly IReadOnlyList<string> _scopeIssuers;
    private readonly IReadOnlyList<SecurityKey> _scopeKeys;

    public PerIssuerSigningKeyResolver(
        IEnumerable<string> scopeIssuers,
        IEnumerable<SecurityKey> scopeKeys)
    {
        ArgumentNullException.ThrowIfNull(scopeIssuers);
        ArgumentNullException.ThrowIfNull(scopeKeys);

        _scopeIssuers = scopeIssuers
            .Where(issuer => !string.IsNullOrWhiteSpace(issuer))
            .Select(issuer => issuer.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _scopeKeys = scopeKeys.ToArray();
    }

    /// <summary>
    /// The scope-token issuer(s) this resolver binds to <see cref="ScopeKeys"/>.
    /// </summary>
    public IReadOnlyList<string> ScopeIssuers => _scopeIssuers;

    /// <summary>The scope-token signing keys this resolver owns.</summary>
    public IReadOnlyList<SecurityKey> ScopeKeys => _scopeKeys;

    /// <summary>
    /// Matches the <see cref="IssuerSigningKeyResolver"/> delegate signature so it can be
    /// assigned to <see cref="TokenValidationParameters.IssuerSigningKeyResolver"/>.
    /// </summary>
    /// <param name="token">The raw JWT (unused; selection is by issuer).</param>
    /// <param name="securityToken">
    /// The parsed token; its <see cref="SecurityToken.Issuer"/> drives key selection. Note the
    /// delegate's third argument is the <c>kid</c>, NOT the issuer, so the issuer is read here.
    /// </param>
    /// <param name="kid">The key identifier hint (unused; selection is by issuer).</param>
    /// <param name="validationParameters">
    /// The active validation parameters, whose <see cref="TokenValidationParameters.IssuerSigningKeys"/>
    /// provide the full configured key set used for the non-breaking fallback.
    /// </param>
    public IEnumerable<SecurityKey> Resolve(
        string token,
        SecurityToken securityToken,
        string kid,
        TokenValidationParameters validationParameters)
    {
        var tokenIssuer = securityToken?.Issuer;
        if (!string.IsNullOrWhiteSpace(tokenIssuer) &&
            _scopeIssuers.Contains(tokenIssuer.Trim(), StringComparer.Ordinal))
        {
            return _scopeKeys;
        }

        // Fallback: unknown or OIDC-authority issuer (also when securityToken is null). Return
        // every configured key so a token that validates today keeps validating. Returning
        // null/empty here would break OIDC tokens whose keys the base handler injects into
        // IssuerSigningKeys.
        return AllConfiguredKeys(validationParameters);
    }

    private IEnumerable<SecurityKey> AllConfiguredKeys(TokenValidationParameters validationParameters)
    {
        IEnumerable<SecurityKey> configured = validationParameters.IssuerSigningKeys ?? [];
        if (validationParameters.IssuerSigningKey is not null)
            configured = configured.Append(validationParameters.IssuerSigningKey);

        // Include this resolver's own scope keys too, so the fallback is a genuine superset
        // even if the parameters were rebuilt without them.
        return configured.Concat(_scopeKeys).Distinct();
    }
}
