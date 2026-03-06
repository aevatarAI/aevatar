using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.Pipeline;

internal static class SelfEventEnvelopeFactory
{
    private const string RuntimeRetryMetadataPrefix = "aevatar.retry.";

    public static EventEnvelope Create(
        string actorId,
        IMessage evt,
        EventEnvelope? inboundEnvelope = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(evt);

        var envelope = new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(evt),
            PublisherId = actorId,
            Direction = EventDirection.Self,
            CorrelationId = inboundEnvelope?.CorrelationId ?? string.Empty,
            TargetActorId = actorId,
        };

        if (inboundEnvelope != null)
        {
            foreach (var pair in inboundEnvelope.Metadata)
            {
                if (ShouldPropagateInboundMetadata(pair.Key))
                    envelope.Metadata[pair.Key] = pair.Value;
            }
        }

        if (metadata != null)
        {
            foreach (var pair in metadata)
                envelope.Metadata[pair.Key] = pair.Value;
        }

        return envelope;
    }

    private static bool ShouldPropagateInboundMetadata(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        return !key.StartsWith(RuntimeRetryMetadataPrefix, StringComparison.Ordinal);
    }
}
