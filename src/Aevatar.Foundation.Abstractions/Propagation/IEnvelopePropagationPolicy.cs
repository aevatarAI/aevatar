namespace Aevatar.Foundation.Abstractions.Propagation;

/// <summary>
/// Applies propagation rules from inbound to outbound envelope.
/// </summary>
public interface IEnvelopePropagationPolicy
{
    /// <summary>
    /// Applies correlation and metadata propagation.
    /// </summary>
    void Apply(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope);
}
