using Aevatar.Foundation.Abstractions;
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
}
