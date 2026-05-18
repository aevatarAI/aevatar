namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// Configuration for the NyxID relay transport boundary.
/// </summary>
public class NyxIdRelayOptions
{
    /// <summary>
    /// Hard upper bound on a single LLM reply turn (LLM thinking + tool rounds + final
    /// streaming dispatch). 300s gives margin for multi-step tool chains common in the
    /// aevatar Lark bot flow — search a skill, hit a remote endpoint, summarize the result —
    /// without letting a genuine hang pin the run actor turn forever. Set to <c>0</c> or
    /// negative on a deployment that has its own watchdog and prefers no in-process cap;
    /// see <c>AgentRunGAgent.ResolveFallbackTimeout</c>.
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 300;

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
    /// Maximum number of interim (non-final) edit dispatches per turn. Lark refuses message
    /// edits beyond a per-message cap (observed ~20 in mainnet, code 230072
    /// "The message has reached the number of times it can be edited"); once that cap is
    /// reached, even the final edit is rejected and the user sees a truncated reply. Capping
    /// interim chunks here leaves headroom so the final flush always lands. Long replies
    /// freeze on the last interim until the final fires — that is preferable to truncation.
    /// </summary>
    public int StreamingMaxInterimChunks { get; set; } = 15;

    /// <summary>
    /// Placeholder text emitted as the first streaming chunk before the LLM produces any delta.
    /// Guarantees a visible "working" state within the outbound RTT even when the LLM suffers
    /// cold-start, router handoff, or tool-call latency before the first token. Set to empty
    /// to disable and instead wait for the first real delta (slower time-to-first-visible).
    /// </summary>
    public string StreamingPlaceholderText { get; set; } = "…";

    /// <summary>
    /// Routes streaming replies through Lark CardKit 2.0 streaming cards instead of editing a
    /// regular message in place. CardKit element-content updates are not subject to the per-
    /// message edit cap (Lark code 230072) so long replies never need to freeze on the last
    /// interim chunk. Defaults to <c>true</c> so the modern card path is the standard
    /// behaviour for the aevatar Lark bot (Feishu console grants the bot
    /// <c>cardkit:card:read</c> + <c>cardkit:card:write</c>). Deployments that have not been
    /// granted those scopes are not stuck: <see cref="ConversationGAgent"/> watches for the
    /// scope-error / rate-limit / table-limit responses returned by <c>card.create</c> and
    /// transitions the turn to the legacy edit-message sink for the rest of the chunks (see
    /// <c>HandleLarkCardStreamingChunkCoreAsync</c>'s <c>CreationFailed</c> branch). Set this
    /// to <c>false</c> on a deployment that wants to skip the create-card round-trip entirely
    /// (e.g. environments that explicitly want the legacy path or do not run a Lark bot).
    /// </summary>
    public bool StreamingCardKitEnabled { get; set; } = true;

    /// <summary>
    /// Minimum interval between CardKit element-content dispatches, in milliseconds. Defaults
    /// to 200ms — well below the 750ms used by the edit-message path because CardKit accepts
    /// far more updates per card than Lark's edit-message cap allows.
    /// </summary>
    public int StreamingCardKitFlushIntervalMs { get; set; } = 200;
}
