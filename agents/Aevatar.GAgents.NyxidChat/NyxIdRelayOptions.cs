namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Configuration for the NyxID Channel Bot Relay webhook endpoint.
/// </summary>
public sealed class NyxIdRelayOptions
{
    /// <summary>
    /// Maximum time (in seconds) to wait for the agent to produce a complete response.
    /// Default: 120 seconds.
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum number of response characters buffered while waiting for the relay turn
    /// to complete. Excess content is truncated before async delivery.
    /// </summary>
    public int MaxBufferedResponseChars { get; set; } = 16 * 1024;

    /// <summary>
    /// When enabled, relay error replies include a [Debug] block with diagnostic details
    /// (model, route, scope, token, tool warnings, raw error). Set to true during
    /// development/debugging, false in production.
    /// </summary>
    public bool EnableDebugDiagnostics { get; set; }

    /// <summary>
    /// Optional override for the Nyx OIDC discovery document URL.
    /// Defaults to <c>{NyxBaseUrl}/.well-known/openid-configuration</c>.
    /// </summary>
    public string? OidcDiscoveryUrl { get; set; }

    /// <summary>
    /// Optional override for the expected JWT audience.
    /// Defaults to the configured Nyx base URL.
    /// </summary>
    public string? ExpectedAudience { get; set; }

    /// <summary>
    /// Time window in seconds used for JWT lifetime clock skew.
    /// </summary>
    public int JwtClockSkewSeconds { get; set; } = 60;

    /// <summary>
    /// Cache lifetime in seconds for Nyx OIDC discovery + JWKS documents.
    /// </summary>
    public int OidcCacheTtlSeconds { get; set; } = 300;
}
