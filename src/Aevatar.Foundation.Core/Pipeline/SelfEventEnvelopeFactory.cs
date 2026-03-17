using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Pipeline;

internal static class SelfEventEnvelopeFactory
{
    public static EventEnvelope Create(
        string actorId,
        IMessage evt,
        EventEnvelope? inboundEnvelope = null,
        EventEnvelopePublishOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(evt);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(actorId, TopologyAudience.Self),
        };

        if (inboundEnvelope?.Propagation != null)
            envelope.Propagation = inboundEnvelope.Propagation.Clone();

        if (!string.IsNullOrWhiteSpace(envelope.Propagation?.CorrelationId))
        {
            envelope.EnsurePropagation().CorrelationId = envelope.Propagation.CorrelationId;
        }

        if (options?.Propagation != null)
            ApplyPropagationOverrides(envelope.EnsurePropagation(), options.Propagation);

        if (!string.IsNullOrWhiteSpace(options?.Delivery?.DeduplicationOperationId))
            envelope.EnsureRuntime().EnsureDeduplication().OperationId = options.Delivery.DeduplicationOperationId;

        return envelope;
    }

    private static void ApplyPropagationOverrides(
        EnvelopePropagation target,
        EventEnvelopePropagationOverrides overrides)
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
