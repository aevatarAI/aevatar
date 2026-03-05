using Aevatar.Foundation.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming.Serialization;

[GenerateSerializer]
[Immutable]
public struct EventEnvelopeSurrogate
{
    [Id(0)]
    public byte[] ProtobufBytes { get; set; }
}

[RegisterConverter]
public sealed class EventEnvelopeSurrogateConverter
    : IConverter<EventEnvelope, EventEnvelopeSurrogate>
{
    public EventEnvelope ConvertFromSurrogate(in EventEnvelopeSurrogate surrogate) =>
        EventEnvelope.Parser.ParseFrom(surrogate.ProtobufBytes);

    public EventEnvelopeSurrogate ConvertToSurrogate(in EventEnvelope value) =>
        new() { ProtobufBytes = value.ToByteArray() };
}
