namespace Aevatar.Mainnet.Host.Api.Responses;

/// <summary>refactor helper, no behavior change</summary>
internal sealed class ResponsesNyxIdIdentityAssertionOptions
{
    public const string SectionName = "Aevatar:Responses:NyxIdIdentityAssertion";

    public string? OidcDiscoveryUrl { get; set; }

    public string? JwksUri { get; set; }

    public string? Issuer { get; set; }

    public string? ExpectedAudience { get; set; }

    public string? ExpectedServiceId { get; set; }

    public int ClockSkewSeconds { get; set; } = 60;

    public int JwksCacheTtlSeconds { get; set; } = 300;

    public int KidMissRefreshCooldownSeconds { get; set; } = 30;
}
