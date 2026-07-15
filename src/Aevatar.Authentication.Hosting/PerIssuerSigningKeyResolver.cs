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
/// </summary>
internal sealed class PerIssuerSigningKeyResolver
{
    private readonly IReadOnlyList<string> _scopeIssuers;
    private readonly IReadOnlyList<SecurityKey> _scopeKeys;

    public PerIssuerSigningKeyResolver(
        IEnumerable<string> authorityIssuers,
        IEnumerable<string> scopeIssuers,
        IEnumerable<SecurityKey> scopeKeys)
    {
        ArgumentNullException.ThrowIfNull(authorityIssuers);
        ArgumentNullException.ThrowIfNull(scopeIssuers);
        ArgumentNullException.ThrowIfNull(scopeKeys);

        var configuredAuthorityIssuers = NormalizeIssuers(authorityIssuers);
        _scopeIssuers = NormalizeIssuers(scopeIssuers);
        if (configuredAuthorityIssuers.Intersect(_scopeIssuers, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException(
                "Scope service token issuer must be distinct from the configured OIDC authority issuer.");
        }

        _scopeKeys = scopeKeys.ToArray();
    }

    /// <summary>
    /// The scope-token issuer(s) this resolver binds to <see cref="ScopeKeys"/>.
    /// </summary>
    public IReadOnlyList<string> ScopeIssuers => _scopeIssuers;

    /// <summary>The scope-token signing keys this resolver owns.</summary>
    public IReadOnlyList<SecurityKey> ScopeKeys => _scopeKeys;

    /// <summary>
    /// Matches the <see cref="IssuerSigningKeyResolverUsingConfiguration"/> delegate signature
    /// so OIDC discovery keys remain available to the JWT validation pipeline.
    /// </summary>
    /// <param name="token">The raw JWT (unused; selection is by issuer).</param>
    /// <param name="securityToken">
    /// The parsed token; its <see cref="SecurityToken.Issuer"/> drives key selection. Note the
    /// delegate's third argument is the <c>kid</c>, NOT the issuer, so the issuer is read here.
    /// </param>
    /// <param name="kid">The key identifier hint (unused; selection is by issuer).</param>
    /// <param name="validationParameters">The active validation parameters.</param>
    /// <param name="configuration">The authority configuration retrieved through OIDC discovery.</param>
    public IEnumerable<SecurityKey> Resolve(
        string token,
        SecurityToken securityToken,
        string kid,
        TokenValidationParameters validationParameters,
        BaseConfiguration configuration)
    {
        var tokenIssuer = securityToken?.Issuer;
        if (string.IsNullOrWhiteSpace(tokenIssuer))
            return [];

        return ClassifyIssuer(tokenIssuer, configuration) switch
        {
            IssuerKind.Scope => _scopeKeys,
            IssuerKind.Authority => configuration?.SigningKeys ?? [],
            _ => [],
        };
    }

    /// <summary>
    /// Accepts only the exact scope issuer or the exact issuer returned by OIDC discovery.
    /// A token matching both is ambiguous and therefore rejected.
    /// </summary>
    public string ValidateIssuer(
        string issuer,
        SecurityToken securityToken,
        TokenValidationParameters validationParameters,
        BaseConfiguration configuration)
    {
        var normalizedIssuer = issuer?.Trim() ?? string.Empty;
        if (ClassifyIssuer(normalizedIssuer, configuration) is IssuerKind.Scope or IssuerKind.Authority)
            return normalizedIssuer;

        throw new SecurityTokenInvalidIssuerException(
            $"Issuer '{normalizedIssuer}' does not uniquely match the configured scope issuer or discovered OIDC issuer.");
    }

    private IssuerKind ClassifyIssuer(string? issuer, BaseConfiguration? configuration)
    {
        var normalizedIssuer = issuer?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedIssuer))
            return IssuerKind.Invalid;

        var discoveredIssuer = configuration?.Issuer?.Trim();
        var matchesScope = _scopeIssuers.Contains(normalizedIssuer, StringComparer.Ordinal);
        var matchesAuthority = !string.IsNullOrWhiteSpace(discoveredIssuer) &&
                               string.Equals(normalizedIssuer, discoveredIssuer, StringComparison.Ordinal);
        if (matchesScope == matchesAuthority)
            return IssuerKind.Invalid;

        return matchesScope ? IssuerKind.Scope : IssuerKind.Authority;
    }

    private static string[] NormalizeIssuers(IEnumerable<string> issuers) => issuers
        .Where(issuer => !string.IsNullOrWhiteSpace(issuer))
        .Select(issuer => issuer.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private enum IssuerKind
    {
        Invalid,
        Scope,
        Authority,
    }
}
