namespace Aevatar.Foundation.Abstractions.Propagation;

/// <summary>
/// Resolves correlation/causation links between inbound and outbound envelopes.
/// </summary>
public interface ICorrelationLinkPolicy
{
    /// <summary>
    /// Resolves outbound correlation id.
    /// </summary>
    string? ResolveCorrelationId(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope);

    /// <summary>
    /// Resolves direct causation id (one-hop upstream event id).
    /// </summary>
    string? ResolveCausationId(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope);
}
