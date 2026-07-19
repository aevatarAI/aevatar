namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>Validation settings for NyxID proxy identity assertions.</summary>
internal sealed class ResponsesNyxIdIdentityAssertionOptions
{
    public const string SectionName = "Aevatar:Authentication:NyxIdIdentityAssertion";

    /// <summary>
    /// Default NyxID authority/issuer for the mainnet deployment. The proxy signs identity
    /// assertions with <c>iss = NyxID base URL</c>; baking the known value in as the default
    /// keeps the Responses ingress working when the (optional) config section is absent, so the
    /// "issuer is not configured" failure cannot recur. Override via
    /// <c>Aevatar:Authentication:NyxIdIdentityAssertion:Issuer</c>. The OIDC discovery / JWKS URIs
    /// derive from it unless <see cref="OidcDiscoveryUrl"/> / <see cref="JwksUri"/> are set.
    /// </summary>
    public const string DefaultIssuer = "https://nyx-api.chrono-ai.fun";

    /// <summary>
    /// Stable audience agreed with NyxID for assertions minted to the Aevatar API.
    /// </summary>
    public const string DefaultAudience = "urn:aevatar:api";

    public string? OidcDiscoveryUrl { get; set; }

    public string? JwksUri { get; set; }

    public string? Issuer { get; set; } = DefaultIssuer;

    public string? ExpectedAudience { get; set; } = DefaultAudience;

    public string? ExpectedServiceId { get; set; }

    public int ClockSkewSeconds { get; set; } = 30;

    public int MaximumLifetimeSeconds { get; set; } = 60;

    public int JwksCacheTtlSeconds { get; set; } = 300;

    public int KidMissRefreshCooldownSeconds { get; set; } = 30;
}
