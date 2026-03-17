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
        long? routeTargetCount = null,
        EventEnvelopePublishOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(propagationPolicy);

        propagationPolicy.Apply(envelope, sourceEnvelope);
        TracingContextHelpers.PopulateTraceId(envelope, overwrite: true);

        var route = envelope.EnsureRoute();
        if (!string.IsNullOrWhiteSpace(sourceActorId) && string.IsNullOrWhiteSpace(route.PublisherActorId))
            route.PublisherActorId = sourceActorId;

        var runtime = envelope.EnsureRuntime();
        if (!string.IsNullOrWhiteSpace(sourceActorId) && string.IsNullOrWhiteSpace(runtime.SourceActorId))
            runtime.SourceActorId = sourceActorId;

        if (routeTargetCount.HasValue)
            runtime.RouteTargetCount = routeTargetCount.Value;

        if (options?.Propagation != null)
            MergePropagation(envelope.EnsurePropagation(), options.Propagation);

        if (!string.IsNullOrWhiteSpace(options?.Delivery?.DeduplicationOperationId))
            runtime.EnsureDeduplication().OperationId = options.Delivery.DeduplicationOperationId;
    }

    private static void MergePropagation(EnvelopePropagation target, EventEnvelopePropagationOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(overrides);

        if (!string.IsNullOrWhiteSpace(overrides.CorrelationId))
            target.CorrelationId = overrides.CorrelationId;
        if (!string.IsNullOrWhiteSpace(overrides.CausationEventId))
            target.CausationEventId = overrides.CausationEventId;
        if (overrides.Trace != null)
            target.Trace = overrides.Trace.Clone();

        foreach (var pair in overrides.Baggage)
            target.Baggage[pair.Key] = pair.Value;
    }
}
