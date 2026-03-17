namespace Aevatar.Foundation.Abstractions.Propagation;

/// <summary>
/// Applies propagation rules from inbound to outbound envelope.
/// </summary>
public interface IEnvelopePropagationPolicy
{
    /// <summary>
    /// Applies correlation and propagation-context inheritance.
    /// </summary>
    void Apply(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope);
}
