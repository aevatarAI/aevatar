using Aevatar.Foundation.Abstractions.Propagation;

namespace Aevatar.Foundation.Core.Propagation;

/// <summary>
/// Default framework propagation policy for typed propagation context.
/// </summary>
public sealed class DefaultEnvelopePropagationPolicy : IEnvelopePropagationPolicy
{
    private readonly ICorrelationLinkPolicy _correlationLinkPolicy;

    public DefaultEnvelopePropagationPolicy(ICorrelationLinkPolicy correlationLinkPolicy)
    {
        _correlationLinkPolicy = correlationLinkPolicy;
    }

    public void Apply(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope)
    {
        ArgumentNullException.ThrowIfNull(outboundEnvelope);

        var outboundPropagation = outboundEnvelope.EnsurePropagation();
        var inboundPropagation = inboundEnvelope?.Propagation;

        if (inboundPropagation != null)
            CopyPropagation(outboundPropagation, inboundPropagation);

        var correlationId = _correlationLinkPolicy.ResolveCorrelationId(outboundEnvelope, inboundEnvelope);
        if (!string.IsNullOrWhiteSpace(correlationId))
            outboundPropagation.CorrelationId = correlationId;

        var causationId = _correlationLinkPolicy.ResolveCausationId(outboundEnvelope, inboundEnvelope);
        if (!string.IsNullOrWhiteSpace(causationId))
            outboundPropagation.CausationEventId = causationId;
    }

    private static void CopyPropagation(EnvelopePropagation outboundPropagation, EnvelopePropagation inboundPropagation)
    {
        ArgumentNullException.ThrowIfNull(outboundPropagation);
        ArgumentNullException.ThrowIfNull(inboundPropagation);

        if (!string.IsNullOrWhiteSpace(inboundPropagation.CorrelationId) &&
            string.IsNullOrWhiteSpace(outboundPropagation.CorrelationId))
        {
            outboundPropagation.CorrelationId = inboundPropagation.CorrelationId;
        }

        if (!string.IsNullOrWhiteSpace(inboundPropagation.CausationEventId) &&
            string.IsNullOrWhiteSpace(outboundPropagation.CausationEventId))
        {
            outboundPropagation.CausationEventId = inboundPropagation.CausationEventId;
        }

        if (inboundPropagation.Trace != null && outboundPropagation.Trace == null)
            outboundPropagation.Trace = inboundPropagation.Trace.Clone();

        foreach (var pair in inboundPropagation.Baggage)
        {
            if (!outboundPropagation.Baggage.ContainsKey(pair.Key))
                outboundPropagation.Baggage[pair.Key] = pair.Value;
        }
    }
}
