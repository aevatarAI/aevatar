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
    /// The public URL of the relay webhook endpoint. Auto-derived from the server's own
    /// address at startup. The agent uses this to auto-configure API keys with callback_url.
    /// Example: "https://aevatar.example.com/api/webhooks/nyxid-relay"
    /// </summary>
    public string? RelayCallbackUrl { get; set; }
}
