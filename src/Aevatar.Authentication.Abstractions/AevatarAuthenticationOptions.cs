namespace Aevatar.Authentication.Abstractions;

/// <summary>
/// Configuration for Aevatar JWT Bearer authentication.
/// Provider-agnostic: Authority points to any OIDC-compliant issuer.
/// </summary>
public sealed class AevatarAuthenticationOptions
{
    public const string SectionName = "Aevatar:Authentication";

    /// <summary>Enable JWT Bearer authentication. Default: false.</summary>
    public bool Enabled { get; set; }

    /// <summary>OIDC discovery authority URL (e.g. "https://idp.example.com").</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>Expected JWT audience. Empty means audience validation is skipped.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Whether to require HTTPS for the authority metadata endpoint. Default: true.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;
}
