using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;

namespace Aevatar.Foundation.VoicePresence.Projection;

public sealed class VoiceRealtimeSessionEventCodec : IProjectionSessionEventCodec<VoiceRealtimeFrame>
{
    public string Channel => "voice-realtime";

    public string GetEventType(VoiceRealtimeFrame evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.FrameCase.ToString();
    }

    public ByteString Serialize(VoiceRealtimeFrame evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        return evt.ToByteString();
    }

    public VoiceRealtimeFrame? Deserialize(string eventType, ByteString payload)
    {
        if (payload == null || payload.IsEmpty)
            return null;

        return VoiceRealtimeFrame.Parser.ParseFrom(payload);
    }
}
