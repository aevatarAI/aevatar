namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Describes whether a payload redactor is currently healthy enough for ingress traffic.
/// </summary>
public enum HealthStatus
{
    /// <summary>
    /// The redactor is healthy and can process ingress traffic normally.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// The redactor is partially available but should remain out of the main ingress path.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// The redactor is unavailable and ingress should remain fail-closed.
    /// </summary>
    Unhealthy = 2,
}
