namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>refactor helper, no behavior change</summary>
internal sealed class ResponsesNyxIdIdentityAssertionOptions
{
    public const string SectionName = "Aevatar:Responses:NyxIdIdentityAssertion";

    /// <summary>
    /// Default NyxID authority/issuer for the mainnet deployment. The proxy signs identity
    /// assertions with <c>iss = NyxID base URL</c>; baking the known value in as the default
    /// keeps the Responses ingress working when the (optional) config section is absent, so the
    /// "issuer is not configured" failure cannot recur. Override via
    /// <c>Aevatar:Responses:NyxIdIdentityAssertion:Issuer</c>. The OIDC discovery / JWKS URIs
    /// derive from it unless <see cref="OidcDiscoveryUrl"/> / <see cref="JwksUri"/> are set.
    /// </summary>
    public const string DefaultIssuer = "https://nyx-api.chrono-ai.fun";

    public string? OidcDiscoveryUrl { get; set; }

    public string? JwksUri { get; set; }

    public string? Issuer { get; set; } = DefaultIssuer;

    public string? ExpectedAudience { get; set; }

    public string? ExpectedServiceId { get; set; }

    public int ClockSkewSeconds { get; set; } = 60;

    public int JwksCacheTtlSeconds { get; set; } = 300;

    public int KidMissRefreshCooldownSeconds { get; set; } = 30;
}
