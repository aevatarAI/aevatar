using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Hosting;
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
            SessionAccepted = new VoiceTransportSessionAccepted
            {
                ActorId = "agent-1",
                ModuleName = "voice_presence",
            SessionId = "session-1",
            PcmSampleRateHz = 24000,
            ObservedStateVersion = 5,
            WireContractVersion = VoiceWireContractDefaults.CurrentWireContractVersion,
            InputImagePolicy = VoiceWireContractDefaults.CreateInputImagePolicy(),
            AttachOutcome = VoiceTransportAttachOutcome.NewSession,
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
            Owner = VoiceToolOwner.Client,
        });

        var parsedControl = VoiceControlFrame.Parser.ParseFrom(controlFrame.ToByteArray());
        parsedControl.ShouldBe(controlFrame);
        parsedControl.SessionAccepted.WireContractVersion.ShouldBe(VoiceWireContractDefaults.CurrentWireContractVersion);
        parsedControl.SessionAccepted.InputImagePolicy.MaxBytes.ShouldBe(VoiceWireContractDefaults.MaxInputImageBytes);
        parsedControl.SessionAccepted.InputImagePolicy.AllowedMediaTypes
            .ShouldBe(VoiceWireContractDefaults.SupportedInputImageMediaTypes);
        parsedControl.SessionAccepted.AttachOutcome.ShouldBe(VoiceTransportAttachOutcome.NewSession);

        providerConfig.Clone().ShouldBe(providerConfig);
        sessionConfig.Clone().ShouldBe(sessionConfig);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoiceProviderEvent));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoiceControlFrame));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoiceToolDefinition));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoicePresenceEventDedupeFenceEntry));
        sessionConfig.ToolDefinitions[0].Owner.ShouldBe(VoiceToolOwner.Client);
        VoiceToolDefinition.Descriptor.Fields["owner"].FieldNumber.ShouldBe(4);
        VoiceTransportSessionAccepted.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .ShouldContain("wire_contract_version");
        VoiceTransportSessionAccepted.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .ShouldContain("input_image_policy");
        VoiceTransportSessionAccepted.Descriptor.Fields["attach_outcome"].FieldNumber.ShouldBe(8);
        ((int)VoiceTransportAttachOutcome.Unspecified).ShouldBe(0);
        ((int)VoiceTransportAttachOutcome.NewSession).ShouldBe(1);
        ((int)VoiceTransportAttachOutcome.Restarted).ShouldBe(2);
        VoiceInputImagePolicy.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .ShouldBe([
                "max_bytes",
                "allowed_media_types",
            ]);
    }

    [Fact]
    public void VoiceFunctionCallOutput_should_roundtrip_through_control_frame_and_runtime_state()
    {
        var output = new VoiceFunctionCallOutput
        {
            CallId = "call-client-1",
            ToolName = "edge.light.toggle",
            OutputJson = """{"ok":true}""",
        };
        var controlFrame = new VoiceControlFrame
        {
            FunctionCallOutput = output.Clone(),
        };
        var pendingCall = new VoicePendingClientToolCall
        {
            CallId = "call-client-1",
            ToolName = "edge.light.toggle",
            ArgumentsJson = """{"room":"entry"}""",
            ResponseId = 7,
            ProviderResponseId = "provider-r7",
            SessionId = "session-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseEpoch = 4,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(10)),
        };
        var runtimeState = new VoicePresenceRuntimeState();
        runtimeState.PendingClientToolCalls.Add(pendingCall.Clone());

        var parsedControl = VoiceControlFrame.Parser.ParseFrom(controlFrame.ToByteArray());
        var parsedState = VoicePresenceRuntimeState.Parser.ParseFrom(runtimeState.ToByteArray());

        parsedControl.ShouldBe(controlFrame);
        parsedControl.FrameCase.ShouldBe(VoiceControlFrame.FrameOneofCase.FunctionCallOutput);
        parsedControl.FunctionCallOutput.ResultCase.ShouldBe(VoiceFunctionCallOutput.ResultOneofCase.OutputJson);
        parsedControl.FunctionCallOutput.OutputJson.ShouldBe("""{"ok":true}""");
        parsedState.PendingClientToolCalls.ShouldHaveSingleItem().ShouldBe(pendingCall);
        VoiceControlFrame.Descriptor.Oneofs.Single(static oneof => oneof.Name == "frame").Fields
            .Single(static field => field.Name == "function_call_output")
            .FieldNumber.ShouldBe(5);
        VoicePresenceRuntimeState.Descriptor.Fields["pending_client_tool_calls"].FieldNumber.ShouldBe(24);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(static x => x.Name)
            .ShouldContain(nameof(VoiceFunctionCallOutput));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(static x => x.Name)
            .ShouldContain(nameof(VoicePendingClientToolCall));
    }

    [Fact]
    public void VoiceModuleSignal_should_roundtrip_client_tool_timeout_with_tag_20()
    {
        var timeout = new VoiceClientToolCallTimeoutExpired
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseEpoch = 14,
            CallId = "call-client-1",
            ToolName = "edge.light.toggle",
        };
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ClientToolCallTimeoutExpired = timeout,
        };

        var parsed = VoiceModuleSignal.Parser.ParseFrom(signal.ToByteArray());
        var signalOneof = VoiceModuleSignal.Descriptor.Oneofs
            .Single(static oneof => oneof.Name == "signal");
        var timeoutField = signalOneof.Fields
            .Single(static field => field.Name == "client_tool_call_timeout_expired");

        parsed.ShouldBe(signal);
        parsed.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.ClientToolCallTimeoutExpired);
        parsed.ClientToolCallTimeoutExpired.ShouldBe(timeout);
        timeoutField.FieldNumber.ShouldBe(20);
        timeoutField.MessageType.Name.ShouldBe(nameof(VoiceClientToolCallTimeoutExpired));
    }

    [Fact]
    public void VoiceCapabilityAndLeaseMessages_ShouldRoundtripAndExposeReflection()
    {
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = "voice-tool:ref-1",
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            CallerScopeId = "scope-1",
            CallerSubject = "caller-1",
            OwnerSubject = "owner-1",
            ChannelPlatform = "nyxid",
        };
        toolContext.AllowedToolNames.Add("doorbell.open");
        var leaseRequested = new VoicePresenceSessionLeaseRequested
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            ExpiresAt = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            ToolContext = toolContext.Clone(),
        };
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            SessionLeaseRequested = leaseRequested,
        };
        var runtimeState = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            ActiveToolContext = toolContext.Clone(),
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
        VoicePresenceRuntimeState.Parser.ParseFrom(runtimeState.ToByteArray()).ShouldBe(runtimeState);
        VoicePresenceCapabilityReadModel.Parser.ParseFrom(capability.ToByteArray()).ShouldBe(capability);
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.SessionLeaseRequested);
        signal.SessionLeaseRequested.ToolContext.CredentialRef.ShouldBe("voice-tool:ref-1");
        runtimeState.ActiveToolContext.CredentialRef.ShouldBe("voice-tool:ref-1");
        capability.RemoteAudioSupport.ShouldBe(VoiceRemoteAudioSupport.LocalOnly);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoicePresenceCapabilityReadModel));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoicePresenceSessionLeaseRequested));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(x => x.Name)
            .ShouldContain(nameof(VoiceToolExecutionContext));
        VoiceToolExecutionContext.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .ShouldNotContain("nyx_id_access_token");
        VoicePresenceSessionLeaseRequested.Descriptor.Fields["tool_context"].MessageType.Name
            .ShouldBe(nameof(VoiceToolExecutionContext));
        VoicePresenceRuntimeState.Descriptor.Fields["active_tool_context"].MessageType.Name
            .ShouldBe(nameof(VoiceToolExecutionContext));
    }

    [Fact]
    public void Voice_presence_proto_should_not_expose_raw_pcm_actor_messages()
    {
        var messageNames = VoicePresenceReflection.Descriptor.MessageTypes
            .Select(static x => x.Name)
            .ToArray();
        var providerEventFields = VoiceProviderEvent.Descriptor.Fields.InDeclarationOrder()
            .Select(static x => x.Name)
            .ToArray();
        var signalFields = VoiceModuleSignal.Descriptor.Fields.InDeclarationOrder()
            .Select(static x => x.Name)
            .ToArray();

        messageNames.ShouldNotContain("VoiceAudioReceived");
        messageNames.ShouldNotContain("VoiceTransportAudioFrameReceived");
        providerEventFields.ShouldNotContain("audio_received");
        signalFields.ShouldNotContain("transport_audio_frame_received");
    }

    [Fact]
    public void VoiceModuleSignal_should_roundtrip_transport_lifetime_completed()
    {
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        var completed = new VoiceTransportLifetimeCompleted
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseExpiresAt = expiresAt,
            Reason = "host_transport_completed",
            LeaseEpoch = 8,
        };
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLifetimeCompleted = completed,
        };

        var parsed = VoiceModuleSignal.Parser.ParseFrom(signal.ToByteArray());

        parsed.ShouldBe(signal);
        parsed.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportLifetimeCompleted);
        parsed.TransportLifetimeCompleted.LeaseEpoch.ShouldBe(8);
        parsed.TransportLifetimeCompleted.ShouldBe(completed);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(static x => x.Name)
            .ShouldContain(nameof(VoiceTransportLifetimeCompleted));
    }

    [Fact]
    public void VoiceModuleSignal_should_roundtrip_input_image_received()
    {
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        var image = new VoiceInputImageReceived
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseExpiresAt = expiresAt,
            LeaseEpoch = 10,
            InputImage = new VoiceInputImage
            {
                MediaType = "image/png",
                Data = ByteString.CopyFrom([1, 2, 3]),
            },
        };
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            InputImageReceived = image,
        };

        var parsed = VoiceModuleSignal.Parser.ParseFrom(signal.ToByteArray());

        parsed.ShouldBe(signal);
        parsed.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.InputImageReceived);
        parsed.InputImageReceived.InputImage.Data.ToByteArray().ShouldBe([1, 2, 3]);
        VoicePresenceReflection.Descriptor.MessageTypes.Select(static x => x.Name)
            .ShouldContain(nameof(VoiceInputImage));
        VoicePresenceReflection.Descriptor.MessageTypes.Select(static x => x.Name)
            .ShouldContain(nameof(VoiceInputImageReceived));
    }

    [Fact]
    public void VoiceModuleSignal_should_roundtrip_transport_lease_renew_requested()
    {
        var renewExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        var renew = new VoiceTransportLeaseRenewRequested
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseEpoch = 12,
            RenewExpiresAt = renewExpiresAt,
        };
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLeaseRenewRequested = renew,
        };

        var parsed = VoiceModuleSignal.Parser.ParseFrom(signal.ToByteArray());
        var signalOneof = VoiceModuleSignal.Descriptor.Oneofs
            .Single(static oneof => oneof.Name == "signal");
        var renewField = signalOneof.Fields
            .Single(static field => field.Name == "transport_lease_renew_requested");

        parsed.ShouldBe(signal);
        parsed.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportLeaseRenewRequested);
        parsed.TransportLeaseRenewRequested.ShouldBe(renew);
        renewField.FieldNumber.ShouldBe(18);
        renewField.MessageType.Name.ShouldBe(nameof(VoiceTransportLeaseRenewRequested));
        VoiceTransportLeaseRenewRequested.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .ShouldNotContain("previous_lease_expires_at");
    }

    [Fact]
    public void VoiceModuleSignal_should_roundtrip_drain_timeout_expired_with_tag_19()
    {
        var timeout = new VoiceDrainTimeoutExpired
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseEpoch = 14,
            ResponseId = 5,
        };
        var signal = new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            DrainTimeoutExpired = timeout,
        };

        var parsed = VoiceModuleSignal.Parser.ParseFrom(signal.ToByteArray());
        var signalOneof = VoiceModuleSignal.Descriptor.Oneofs
            .Single(static oneof => oneof.Name == "signal");
        var timeoutField = signalOneof.Fields
            .Single(static field => field.Name == "drain_timeout_expired");

        parsed.ShouldBe(signal);
        parsed.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.DrainTimeoutExpired);
        parsed.DrainTimeoutExpired.ShouldBe(timeout);
        timeoutField.FieldNumber.ShouldBe(19);
        timeoutField.MessageType.Name.ShouldBe(nameof(VoiceDrainTimeoutExpired));
        VoiceDrainTimeoutExpired.Descriptor.Fields.InDeclarationOrder()
            .Select(static field => field.Name)
            .ShouldBe([
                "session_id",
                "owner_id",
                "transport_lease_id",
                "lease_epoch",
                "response_id",
            ]);
    }

    [Fact]
    public void VoicePresenceSessionDispatch_should_wrap_transport_control_self_signal()
    {
        var control = new VoiceTransportControlFrameReceived
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            LeaseEpoch = 9,
            ControlFrame = new VoiceControlFrame
            {
                DrainAcknowledged = new VoiceDrainAcknowledged
                {
                    ResponseId = 3,
                    PlayoutSequence = 4,
                },
            },
        };

        var envelope = VoicePresenceSessionDispatch.BuildSelfEnvelope("voice-agent", "voice_presence", control);
        var signal = envelope.Payload.Unpack<VoiceModuleSignal>();

        envelope.Route.ShouldBe(EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self));
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportControlFrameReceived);
        signal.TransportControlFrameReceived.LeaseEpoch.ShouldBe(9);
        signal.TransportControlFrameReceived.ShouldBe(control);
        signal.TransportControlFrameReceived.ShouldNotBeSameAs(control);
    }

    [Fact]
    public void VoicePresenceSessionDispatch_should_wrap_input_image_direct_signal()
    {
        var image = new VoiceInputImageReceived
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            LeaseEpoch = 11,
            InputImage = new VoiceInputImage
            {
                MediaType = "image/jpeg",
                Data = ByteString.CopyFrom([4, 5, 6]),
            },
        };

        var envelope = VoicePresenceSessionDispatch.BuildDirectEnvelope("voice-agent", "voice_presence", image);
        var signal = envelope.Payload.Unpack<VoiceModuleSignal>();

        envelope.Route.ShouldBe(EnvelopeRouteSemantics.CreateDirect(VoicePresenceSessionDispatch.HostPublisherId, "voice-agent"));
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.InputImageReceived);
        signal.InputImageReceived.ShouldBe(image);
        signal.InputImageReceived.ShouldNotBeSameAs(image);
    }

    [Fact]
    public void VoicePresenceSessionDispatch_should_wrap_transport_lease_renew_direct_signal()
    {
        var renew = new VoiceTransportLeaseRenewRequested
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseEpoch = 13,
            RenewExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
        };

        var envelope = VoicePresenceSessionDispatch.BuildDirectEnvelope("voice-agent", "voice_presence", renew);
        var signal = envelope.Payload.Unpack<VoiceModuleSignal>();

        envelope.Route.ShouldBe(EnvelopeRouteSemantics.CreateDirect(VoicePresenceSessionDispatch.HostPublisherId, "voice-agent"));
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.TransportLeaseRenewRequested);
        signal.TransportLeaseRenewRequested.ShouldBe(renew);
        signal.TransportLeaseRenewRequested.ShouldNotBeSameAs(renew);
    }

    [Fact]
    public void VoicePresenceSessionDispatch_should_wrap_drain_timeout_direct_signal()
    {
        var timeout = new VoiceDrainTimeoutExpired
        {
            SessionId = "lease-1",
            OwnerId = "host-1",
            TransportLeaseId = "transport-1",
            LeaseEpoch = 14,
            ResponseId = 5,
        };

        var envelope = VoicePresenceSessionDispatch.BuildDirectEnvelope("voice-agent", "voice_presence", timeout);
        var signal = envelope.Payload.Unpack<VoiceModuleSignal>();

        envelope.Route.ShouldBe(EnvelopeRouteSemantics.CreateDirect(VoicePresenceSessionDispatch.HostPublisherId, "voice-agent"));
        signal.ModuleName.ShouldBe("voice_presence");
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.DrainTimeoutExpired);
        signal.DrainTimeoutExpired.ShouldBe(timeout);
        signal.DrainTimeoutExpired.ShouldNotBeSameAs(timeout);
    }
}
