using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceProtoTests
{
    [Fact]
    public void VoiceProviderEvent_ShouldRoundtripAndCoverMergePaths()
    {
        var providerEvent = new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 7 },
        };

        var parsed = VoiceProviderEvent.Parser.ParseFrom(providerEvent.ToByteArray());
        parsed.ShouldBe(providerEvent);
        parsed.EventCase.ShouldBe(VoiceProviderEvent.EventOneofCase.ResponseStarted);
        parsed.ResponseStarted.ResponseId.ShouldBe(7);

        var merged = new VoiceProviderEvent();
        merged.MergeFrom(providerEvent);
        merged.ShouldBe(providerEvent);
        merged.MergeFrom((VoiceProviderEvent)null!);
        merged.Equals((object?)null).ShouldBeFalse();
    }

    [Fact]
    public void VoiceControlAndConfigMessages_ShouldRoundtripAndExposeReflection()
    {
        var controlFrame = new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 3,
                PlayoutSequence = 42,
            },
        };
        var providerConfig = new VoiceProviderConfig
        {
            ProviderName = "openai",
            Endpoint = "wss://example.test/realtime",
            ApiKey = "sk-test",
            Model = "gpt-realtime",
        };
        var sessionConfig = new VoiceSessionConfig
        {
            Voice = "alloy",
            Instructions = "stay concise",
            SampleRateHz = 24000,
        };
        sessionConfig.ToolNames.Add("doorbell.open");

        var parsedControl = VoiceControlFrame.Parser.ParseFrom(controlFrame.ToByteArray());
        parsedControl.ShouldBe(controlFrame);
        parsedControl.DrainAcknowledged.ResponseId.ShouldBe(3);
        parsedControl.DrainAcknowledged.PlayoutSequence.ShouldBe(42);

        providerConfig.Clone().ShouldBe(providerConfig);
        sessionConfig.Clone().ShouldBe(sessionConfig);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoiceProviderEvent));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoiceControlFrame));
    }
}
