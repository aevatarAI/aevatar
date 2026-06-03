using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Projection;
using Google.Protobuf;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public sealed class VoiceRealtimeSessionEventCodecTests
{
    [Fact]
    public void Codec_should_expose_channel_event_type_and_roundtrip_payload()
    {
        var codec = new VoiceRealtimeSessionEventCodec();
        var frame = CreateTranscriptDeltaFrame();

        codec.Channel.ShouldBe("voice-realtime");
        codec.GetEventType(frame).ShouldBe(VoiceRealtimeFrame.FrameOneofCase.TranscriptDelta.ToString());

        var payload = codec.Serialize(frame);
        var decoded = codec.Deserialize(codec.GetEventType(frame), payload);

        decoded.ShouldNotBeNull();
        decoded.ShouldBe(frame);
    }

    [Fact]
    public void Codec_should_return_null_for_empty_payload()
    {
        var codec = new VoiceRealtimeSessionEventCodec();

        codec.Deserialize(VoiceRealtimeFrame.FrameOneofCase.TranscriptDelta.ToString(), ByteString.Empty)
            .ShouldBeNull();
        codec.Deserialize(VoiceRealtimeFrame.FrameOneofCase.TranscriptDelta.ToString(), null!)
            .ShouldBeNull();
    }

    [Fact]
    public void Codec_should_ignore_event_type_and_decode_payload_contract()
    {
        var codec = new VoiceRealtimeSessionEventCodec();
        var frame = CreateTranscriptDeltaFrame();

        // Event type is advisory for this codec; the protobuf payload is the contract authority.
        var decoded = codec.Deserialize("DifferentType", codec.Serialize(frame));

        decoded.ShouldNotBeNull();
        decoded.ShouldBe(frame);
    }

    [Fact]
    public void Codec_should_throw_for_malformed_payload()
    {
        var codec = new VoiceRealtimeSessionEventCodec();

        Action act = () => codec.Deserialize(
            VoiceRealtimeFrame.FrameOneofCase.TranscriptDelta.ToString(),
            ByteString.CopyFrom([0x0A, 0xFF]));

        act.ShouldThrow<InvalidProtocolBufferException>();
    }

    [Fact]
    public void Codec_should_validate_event_arguments()
    {
        var codec = new VoiceRealtimeSessionEventCodec();

        Action getEventType = () => codec.GetEventType(null!);
        Action serialize = () => codec.Serialize(null!);

        getEventType.ShouldThrow<ArgumentNullException>()
            .ParamName.ShouldBe("evt");
        serialize.ShouldThrow<ArgumentNullException>()
            .ParamName.ShouldBe("evt");
    }

    private static VoiceRealtimeFrame CreateTranscriptDeltaFrame()
    {
        return new VoiceRealtimeFrame
        {
            ModuleName = "voice_presence",
            SessionId = "session-1",
            TranscriptDelta = new VoiceTranscriptDelta
            {
                Text = "hello",
                ProviderResponseId = "provider-response-1",
                ResponseId = 7,
            },
        };
    }
}
