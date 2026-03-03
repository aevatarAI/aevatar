using Aevatar.Foundation.Abstractions.Propagation;

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
    };

    /// <summary>
    /// Metadata key prefixes that are internal routing/system metadata
    /// and must NOT be propagated from inbound to outbound envelopes.
    /// These are set fresh by the publisher for each new event.
    /// </summary>
    private static readonly string[] BlockedMetadataKeyPrefixes = ["__"];

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

                if (IsBlockedByPrefix(key))
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

    private static bool IsBlockedByPrefix(string key)
    {
        foreach (var prefix in BlockedMetadataKeyPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
