using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public sealed class VoiceRealtimeFrameProtoTests
{
    [Fact]
    public void VoiceRealtimeFrame_should_roundtrip_control_frame()
    {
        var frame = new VoiceRealtimeFrame
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

        var parsed = VoiceRealtimeFrame.Parser.ParseFrom(frame.ToByteArray());

        parsed.ModuleName.ShouldBe("voice_presence");
        parsed.SessionId.ShouldBe("session-1");
        parsed.FrameCase.ShouldBe(VoiceRealtimeFrame.FrameOneofCase.TranscriptDelta);
        parsed.TranscriptDelta.Text.ShouldBe("hello");
        parsed.TranscriptDelta.ProviderResponseId.ShouldBe("provider-response-1");
        parsed.TranscriptDelta.ResponseId.ShouldBe(7);
    }

    [Fact]
    public void VoiceRealtimeFrame_descriptor_should_not_expose_raw_audio_fields()
    {
        var forbidden = new[] { "pcm", "audio", "bytes", "VoiceAudioReceived" };
        var names = EnumerateDescriptorNames(VoiceRealtimeFrame.Descriptor).ToArray();

        foreach (var token in forbidden)
        {
            names.ShouldNotContain(name =>
                name.Contains(token, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static IEnumerable<string> EnumerateDescriptorNames(MessageDescriptor descriptor)
    {
        yield return descriptor.Name;
        foreach (var field in descriptor.Fields.InDeclarationOrder())
        {
            yield return field.Name;
            yield return field.JsonName;
            yield return field.PropertyName;
            if (field.FieldType == FieldType.Message && field.MessageType != null)
                yield return field.MessageType.Name;
        }
    }
}
