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
    public void VoiceRealtimeFrame_should_roundtrip_display_text_frame()
    {
        var frame = new VoiceRealtimeFrame
        {
            ModuleName = "voice_presence",
            SessionId = "session-1",
            DisplayText = new VoiceDisplayText
            {
                Text = "lights are on",
                ProviderResponseId = "provider-response-1",
                ResponseId = 7,
            },
        };

        var parsed = VoiceRealtimeFrame.Parser.ParseFrom(frame.ToByteArray());

        parsed.FrameCase.ShouldBe(VoiceRealtimeFrame.FrameOneofCase.DisplayText);
        parsed.DisplayText.Text.ShouldBe("lights are on");
        parsed.DisplayText.ProviderResponseId.ShouldBe("provider-response-1");
        parsed.DisplayText.ResponseId.ShouldBe(7);
    }

    [Fact]
    public void VoiceRealtimeFrame_should_roundtrip_display_image_frame()
    {
        var frame = new VoiceRealtimeFrame
        {
            ModuleName = "voice_presence",
            SessionId = "session-1",
            DisplayImage = new VoiceDisplayImage
            {
                MediaType = "image/png",
                Data = ByteString.CopyFrom([1, 2, 3]),
                AltText = "front door",
                ProviderResponseId = "provider-response-2",
                ResponseId = 8,
            },
        };

        var parsed = VoiceRealtimeFrame.Parser.ParseFrom(frame.ToByteArray());

        parsed.FrameCase.ShouldBe(VoiceRealtimeFrame.FrameOneofCase.DisplayImage);
        parsed.DisplayImage.MediaType.ShouldBe("image/png");
        parsed.DisplayImage.Data.ToByteArray().ShouldBe([1, 2, 3]);
        parsed.DisplayImage.AltText.ShouldBe("front door");
        parsed.DisplayImage.ProviderResponseId.ShouldBe("provider-response-2");
        parsed.DisplayImage.ResponseId.ShouldBe(8);
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
