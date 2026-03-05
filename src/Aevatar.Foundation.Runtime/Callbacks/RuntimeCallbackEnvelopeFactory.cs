using System.Globalization;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Callbacks;

public static class RuntimeCallbackEnvelopeFactory
{
    public static EventEnvelope CreateSelfEnvelope(string actorId, EventEnvelope triggerEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(triggerEnvelope);
        ArgumentNullException.ThrowIfNull(triggerEnvelope.Payload);

        var envelope = triggerEnvelope.Clone();
        envelope.Direction = EventDirection.Self;
        envelope.TargetActorId = actorId;
        envelope.PublisherId = actorId;

        if (string.IsNullOrWhiteSpace(envelope.Id))
            envelope.Id = Guid.NewGuid().ToString("N");

        if (envelope.Timestamp == null)
            envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);

        return envelope;
    }

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

        var envelope = CreateSelfEnvelope(actorId, triggerEnvelope);
        envelope.Id = Guid.NewGuid().ToString("N");
        envelope.Timestamp = Timestamp.FromDateTime(DateTime.UtcNow);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackId] = callbackId;
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackGeneration] =
            generation.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFireIndex] =
            fireIndex.ToString(CultureInfo.InvariantCulture);
        envelope.Metadata[RuntimeCallbackMetadataKeys.CallbackFiredAtUnixTimeMs] =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        return envelope;
    }
}
