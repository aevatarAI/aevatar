using Aevatar.CQRS.Projection.Core.Abstractions;
using Google.Protobuf;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyRoomSessionEventCodec
    : IProjectionSessionEventCodec<StreamingProxyRoomSessionEnvelope>
{
    public string Channel => "streaming-proxy-room";

    public string GetEventType(StreamingProxyRoomSessionEnvelope evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return string.IsNullOrWhiteSpace(evt.Envelope?.Payload?.TypeUrl)
            ? StreamingProxyRoomSessionEnvelope.Descriptor.FullName
            : evt.Envelope.Payload.TypeUrl;
    }

    public ByteString Serialize(StreamingProxyRoomSessionEnvelope evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public StreamingProxyRoomSessionEnvelope? Deserialize(string eventType, ByteString payload)
    {
        if (string.IsNullOrWhiteSpace(eventType) || payload == null || payload.IsEmpty)
            return null;

        try
        {
            var envelope = StreamingProxyRoomSessionEnvelope.Parser.ParseFrom(payload);
            return string.Equals(GetEventType(envelope), eventType, StringComparison.Ordinal)
                ? envelope
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }
}
