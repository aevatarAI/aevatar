using Aevatar.Foundation.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

/// <summary>
/// Orleans-serializable surrogate for the protobuf-generated <see cref="EventEnvelope"/>.
/// </summary>
[GenerateSerializer]
public struct EventEnvelopeSurrogate
{
    [Id(0)] public string Id;
    [Id(1)] public byte[] PayloadBytes;
    [Id(2)] public string PublisherId;
    [Id(3)] public int Direction;
    [Id(4)] public string CorrelationId;
    [Id(5)] public string TargetActorId;
    [Id(6)] public Dictionary<string, string> Metadata;
    [Id(7)] public long TimestampSeconds;
    [Id(8)] public int TimestampNanos;
}

[RegisterConverter]
public sealed class EventEnvelopeSurrogateConverter :
    IConverter<EventEnvelope, EventEnvelopeSurrogate>
{
    public EventEnvelope ConvertFromSurrogate(in EventEnvelopeSurrogate surrogate)
    {
        var envelope = new EventEnvelope
        {
            Id = surrogate.Id ?? string.Empty,
            PublisherId = surrogate.PublisherId ?? string.Empty,
            Direction = (EventDirection)surrogate.Direction,
            CorrelationId = surrogate.CorrelationId ?? string.Empty,
            TargetActorId = surrogate.TargetActorId ?? string.Empty,
        };

        if (surrogate.TimestampSeconds != 0 || surrogate.TimestampNanos != 0)
            envelope.Timestamp = new Timestamp { Seconds = surrogate.TimestampSeconds, Nanos = surrogate.TimestampNanos };

        if (surrogate.PayloadBytes is { Length: > 0 })
            envelope.Payload = Any.Parser.ParseFrom(surrogate.PayloadBytes);

        if (surrogate.Metadata is not null)
        {
            foreach (var kv in surrogate.Metadata)
                envelope.Metadata[kv.Key] = kv.Value;
        }

        return envelope;
    }

    public EventEnvelopeSurrogate ConvertToSurrogate(in EventEnvelope value)
    {
        return new EventEnvelopeSurrogate
        {
            Id = value.Id,
            PayloadBytes = value.Payload?.ToByteArray() ?? [],
            PublisherId = value.PublisherId,
            Direction = (int)value.Direction,
            CorrelationId = value.CorrelationId,
            TargetActorId = value.TargetActorId,
            Metadata = new Dictionary<string, string>(value.Metadata),
            TimestampSeconds = value.Timestamp?.Seconds ?? 0,
            TimestampNanos = value.Timestamp?.Nanos ?? 0,
        };
    }
}
