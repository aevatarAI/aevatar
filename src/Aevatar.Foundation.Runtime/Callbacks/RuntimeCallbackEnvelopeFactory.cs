using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Callbacks;

public static class RuntimeCallbackEnvelopeFactory
{
    public static EventEnvelope CreateFiredEnvelope(
        string actorId,
        string callbackId,
        long generation,
        long fireIndex,
        EventEnvelope triggerEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(generation, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(fireIndex, 0);

        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        ArgumentNullException.ThrowIfNull(triggerEnvelope.Payload);

        var envelope = triggerEnvelope.Clone();
        envelope.Id = Guid.NewGuid().ToString("N");
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.TargetActorId = actorId;
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackId] = callbackId;
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackGeneration] =
            generation.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFireIndex] =
            fireIndex.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs] =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return envelope;
    }

    public static EventEnvelope CreateScheduledEnvelope(
        string actorId,
        string callbackId,
        long generation,
        long fireIndex,
        EventEnvelope triggerEnvelope,
        RuntimeCallbackDeliveryMode deliveryMode)
    {
        return deliveryMode switch
        {
            RuntimeCallbackDeliveryMode.FiredSelfEvent => CreateFiredEnvelope(
                actorId,
                callbackId,
                generation,
                fireIndex,
                triggerEnvelope),
            RuntimeCallbackDeliveryMode.EnvelopeRedelivery => CreateEnvelopeRedelivery(actorId, triggerEnvelope),
            _ => throw new ArgumentOutOfRangeException(nameof(deliveryMode), deliveryMode, "Unknown callback delivery mode."),
        };
    }

    private static EventEnvelope CreateEnvelopeRedelivery(
        string actorId,
        EventEnvelope triggerEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        ArgumentNullException.ThrowIfNull(triggerEnvelope.Payload);

        var envelope = triggerEnvelope.Clone();
        if (string.IsNullOrWhiteSpace(envelope.TargetActorId))
            envelope.TargetActorId = actorId;
        return envelope;
    }
}
