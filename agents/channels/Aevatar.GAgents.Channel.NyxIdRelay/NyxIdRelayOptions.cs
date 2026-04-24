namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Configuration for the NyxID relay transport boundary.
/// </summary>
public class NyxIdRelayOptions
{
    public int ResponseTimeoutSeconds { get; set; } = 120;

    public int MaxBufferedResponseChars { get; set; } = 16 * 1024;

    public bool EnableDebugDiagnostics { get; set; }

    public string? OidcDiscoveryUrl { get; set; }

    public string? CallbackExpectedAudience { get; set; }

    public int JwtClockSkewSeconds { get; set; } = 60;

    public int OidcCacheTtlSeconds { get; set; } = 300;

    public int JwksKidMissRefreshCooldownSeconds { get; set; } = 10;

    public string? WebhookBaseUrl { get; set; }

    public bool RequireMessageIdHeader { get; set; } = true;

    public int RelayReplyTokenRuntimeTtlSeconds { get; set; } = 30 * 60;

    public bool InteractiveRepliesEnabled { get; set; } = true;
}
