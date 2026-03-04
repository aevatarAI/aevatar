using Aevatar.Foundation.Abstractions.Propagation;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Observability;

namespace Aevatar.Foundation.Runtime.Propagation;

public static class EnvelopePublishContextHelpers
{
    public static void ApplyOutboundPublishContext(
        EventEnvelope envelope,
        EventEnvelope? sourceEnvelope,
        IEnvelopePropagationPolicy propagationPolicy,
        string sourceActorId,
        long? routeTargetCount = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(propagationPolicy);

        propagationPolicy.Apply(envelope, sourceEnvelope);
        TracingContextHelpers.PopulateTraceId(envelope, overwrite: true);

        if (!string.IsNullOrWhiteSpace(sourceActorId))
            envelope.Metadata[EnvelopeMetadataKeys.SourceActorId] = sourceActorId;

        if (routeTargetCount.HasValue)
            envelope.Metadata[EnvelopeMetadataKeys.RouteTargetCount] = routeTargetCount.Value.ToString();
    }
}
