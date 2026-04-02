namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Configuration for the NyxID Channel Bot Relay webhook endpoint.
/// </summary>
public sealed class NyxIdRelayOptions
{
    /// <summary>
    /// HMAC-SHA256 shared secret for verifying webhook signatures from NyxID.
    /// Must match the webhook_secret configured in the NyxID API key's callback settings.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Maximum time (in seconds) to wait for the agent to produce a complete response.
    /// Default: 120 seconds.
    /// </summary>
    public int ResponseTimeoutSeconds { get; set; } = 120;
}
