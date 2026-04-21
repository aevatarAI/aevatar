namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Removes short-lived credentials and non-required sensitive fields from one raw channel payload before ingress commit.
/// </summary>
public interface IPayloadRedactor
{
    /// <summary>
    /// Produces the sanitized payload that may continue through the durable ingress path.
    /// </summary>
    Task<RedactionResult> RedactAsync(ChannelId channel, byte[] rawPayload, CancellationToken ct);

    /// <summary>
    /// Probes whether the redactor can safely re-enter the ingress path.
    /// </summary>
    Task<HealthStatus> HealthCheckAsync(CancellationToken ct);
}
