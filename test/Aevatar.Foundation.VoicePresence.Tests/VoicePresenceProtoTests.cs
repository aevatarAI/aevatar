using Aevatar.Foundation.VoicePresence.Abstractions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
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
        sessionConfig.ToolDefinitions.Add(new VoiceToolDefinition
        {
            Name = "door.close",
            Description = "close the front door",
            ParametersSchema = """{"type":"object"}""",
        });

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
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoiceToolDefinition));
    }

    [Fact]
    public void VoiceCapabilityAndLeaseMessages_ShouldRoundtripAndExposeReflection()
    {
        var leaseRequested = new VoicePresenceSessionLeaseRequested
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            SessionLeaseRequested = leaseRequested,
        };
        var capability = new VoicePresenceCapabilityReadModel
        {
            Id = "agent-1:voice_presence",
            ActorId = "agent-1",
            ModuleName = "voice_presence",
            StateVersion = 3,
            LastEventId = "event-3",
            UpdatedAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Initialized = true,
            PcmSampleRateHz = 24000,
            ActiveSessionId = "lease-1",
            RemoteAudioSupport = VoiceRemoteAudioSupport.LocalOnly,
        };

        VoiceModuleSignal.Parser.ParseFrom(signal.ToByteArray()).ShouldBe(signal);
        VoicePresenceCapabilityReadModel.Parser.ParseFrom(capability.ToByteArray()).ShouldBe(capability);
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.SessionLeaseRequested);
        capability.RemoteAudioSupport.ShouldBe(VoiceRemoteAudioSupport.LocalOnly);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoicePresenceCapabilityReadModel));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoicePresenceSessionLeaseRequested));
    }

    [Fact]
    public void VoiceModuleSignal_should_roundtrip_transport_audio_frame_received()
    {
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportAudioFrameReceived = new VoiceTransportAudioFrameReceived
            {
                SessionId = "lease-1",
                OwnerId = "host-1",
                TransportLeaseId = "transport-1",
                LeaseExpiresAt = expiresAt,
                Pcm16 = ByteString.CopyFrom([1, 2, 3]),
                SampleRateHz = 24000,
            },
        };

        var parsed = VoiceModuleSignal.Parser.ParseFrom(signal.ToByteArray());

        parsed.ShouldBe(signal);
        parsed.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportAudioFrameReceived);
        parsed.TransportAudioFrameReceived.Pcm16.ToByteArray().ShouldBe([1, 2, 3]);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(static x => x.Name)
            .ShouldContain(nameof(VoiceTransportAudioFrameReceived));
    }
}
