namespace Aevatar.Authentication.Abstractions;

/// <summary>
/// Configuration for Aevatar JWT Bearer authentication.
/// Provider-agnostic: Authority points to any OIDC-compliant issuer.
/// </summary>
public sealed class AevatarAuthenticationOptions
{
    public const string SectionName = "Aevatar:Authentication";

    /// <summary>
    /// Explicitly enable or disable JWT Bearer authentication.
    /// The host defaults to enabled when this setting is omitted, and only honors
    /// <c>false</c> in Development.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>OIDC discovery authority URL (e.g. "https://idp.example.com").</summary>
    public string Authority { get; set; } = string.Empty;

    /// <summary>
    /// Expected external JWT audience. Required outside Development when authentication is enabled;
    /// Development may leave it empty to opt out of audience validation.
    /// </summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Whether to require HTTPS for the authority metadata endpoint. Default: true.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Explicit allow-list of JWS signing algorithms accepted for bearer tokens.
    /// <para>
    /// When <see langword="null"/> (the default) the host pins a safe superset covering every
    /// algorithm actually accepted (asymmetric OIDC algorithms, plus the scope-token
    /// algorithms when scope service tokens are enabled). Pinning the algorithm set blocks
    /// <c>alg=none</c> and signature-algorithm downgrade attacks without changing which
    /// legitimate tokens validate.
    /// </para>
    /// <para>
    /// Override only when you know the exact set the deployment issues; an over-narrow list
    /// rejects otherwise-valid tokens, so prefer leaving it unset.
    /// </para>
    /// </summary>
    public string[]? ValidAlgorithms { get; set; }

    /// <summary>Optional DPoP (RFC 9449) sender-constraint validation. Disabled by default.</summary>
    public DPoPOptions DPoP { get; set; } = new();
}

/// <summary>
/// DPoP (Demonstration of Proof-of-Possession, RFC 9449) proof-validation settings.
/// Off by default so bearer semantics are unchanged unless a deployment opts in.
/// </summary>
public sealed class DPoPOptions
{
    /// <summary>
    /// When <see langword="true"/>, a validated access token that carries a <c>cnf.jkt</c>
    /// confirmation claim additionally requires a matching <c>DPoP</c> proof header on the
    /// request. Default <see langword="false"/> — the check is a no-op and behavior is
    /// identical to plain bearer.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowed absolute skew, in seconds, between the proof's <c>iat</c> and server time
    /// (freshness window). Default 60.
    /// </summary>
    public int IatSkewSeconds { get; set; } = 60;
}
