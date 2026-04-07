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
    /// When enabled, relay error replies include a [Debug] block with diagnostic details
    /// (model, route, scope, token, tool warnings, raw error). Set to true during
    /// development/debugging, false in production.
    /// </summary>
    public bool EnableDebugDiagnostics { get; set; }
}
