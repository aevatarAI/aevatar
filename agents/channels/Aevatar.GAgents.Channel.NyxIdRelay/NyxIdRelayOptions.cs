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

    public int CallbackReplayWindowSeconds { get; set; } = 6 * 60;

    public string? WebhookBaseUrl { get; set; }

    public bool RequireMessageIdHeader { get; set; } = true;

    public int RelayReplyTokenRuntimeTtlSeconds { get; set; } = 30 * 60;

    public bool InteractiveRepliesEnabled { get; set; } = true;

    /// <summary>
    /// Enables the progressive (streaming) reply path that sends a placeholder message immediately
    /// and edits it in place while the LLM streams. When disabled, the channel runtime falls back
    /// to the legacy single-shot reply after the LLM completes.
    /// </summary>
    public bool StreamingRepliesEnabled { get; set; } = true;

    /// <summary>
    /// Minimum interval between progressive edit dispatches, in milliseconds. The final flush
    /// always bypasses this throttle so the user sees the complete text once the stream ends.
    /// </summary>
    public int StreamingFlushIntervalMs { get; set; } = 750;

    /// <summary>
    /// Placeholder text emitted as the first streaming chunk before the LLM produces any delta.
    /// Guarantees a visible "working" state within the outbound RTT even when the LLM suffers
    /// cold-start, router handoff, or tool-call latency before the first token. Set to empty
    /// to disable and instead wait for the first real delta (slower time-to-first-visible).
    /// </summary>
    public string StreamingPlaceholderText { get; set; } = "…";
}
