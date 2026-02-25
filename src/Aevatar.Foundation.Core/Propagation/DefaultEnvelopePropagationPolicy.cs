using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;

namespace Aevatar.Foundation.Core.Propagation;

/// <summary>
/// Default framework propagation policy based on raw inbound envelope.
/// </summary>
public sealed class DefaultEnvelopePropagationPolicy : IEnvelopePropagationPolicy
{
    private static readonly HashSet<string> BlockedMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "command.id",
        "command_id",
        PublisherChainMetadata.PublishersMetadataKey,
    };

    private readonly ICorrelationLinkPolicy _correlationLinkPolicy;

    public DefaultEnvelopePropagationPolicy(ICorrelationLinkPolicy correlationLinkPolicy)
    {
        _correlationLinkPolicy = correlationLinkPolicy;
    }

    public void Apply(EventEnvelope outboundEnvelope, EventEnvelope? inboundEnvelope)
    {
        ArgumentNullException.ThrowIfNull(outboundEnvelope);

        if (inboundEnvelope != null)
        {
            foreach (var (key, value) in inboundEnvelope.Metadata)
            {
                if (BlockedMetadataKeys.Contains(key))
                    continue;

                outboundEnvelope.Metadata[key] = value;
            }
        }

        var correlationId = _correlationLinkPolicy.ResolveCorrelationId(outboundEnvelope, inboundEnvelope);
        if (!string.IsNullOrWhiteSpace(correlationId))
            outboundEnvelope.CorrelationId = correlationId;

        var causationId = _correlationLinkPolicy.ResolveCausationId(outboundEnvelope, inboundEnvelope);
        if (!string.IsNullOrWhiteSpace(causationId))
            outboundEnvelope.Metadata[EnvelopeMetadataKeys.TraceCausationId] = causationId;
    }
}
