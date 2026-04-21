namespace Aevatar.GAgents.Channel.Abstractions;

/// <summary>
/// Removes short-lived credentials and non-required sensitive fields from one raw channel payload before ingress commit.
/// </summary>
/// <remarks>
/// Redaction is part of the ingress safety boundary. Implementations should fail closed: if payload sanitization or health
/// probing cannot complete safely, ingress should remain blocked rather than persisting unsafe raw payloads.
/// </remarks>
public interface IPayloadRedactor
{
    /// <summary>
    /// Produces the sanitized payload that may continue through the durable ingress path.
    /// </summary>
    /// <param name="channel">The channel whose payload is being sanitized.</param>
    /// <param name="rawPayload">The raw vendor payload bytes received at ingress.</param>
    /// <param name="ct">A token that cancels redaction.</param>
    /// <returns>The sanitized payload and a flag that records whether the bytes were modified.</returns>
    Task<RedactionResult> RedactAsync(ChannelId channel, byte[] rawPayload, CancellationToken ct);

    /// <summary>
    /// Probes whether the redactor can safely re-enter the ingress path.
    /// </summary>
    /// <param name="ct">A token that cancels the health probe.</param>
    /// <returns>The current health state used to decide whether ingress may proceed.</returns>
    Task<HealthStatus> HealthCheckAsync(CancellationToken ct);
}
