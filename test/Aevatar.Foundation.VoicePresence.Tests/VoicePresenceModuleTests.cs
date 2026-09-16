using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Abstractions.Sessions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceModuleTests
{
    private const string DefaultModuleName = "voice_presence";

    [Fact]
    public async Task InitializeAsync_should_be_idempotent_without_opening_provider_session()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(
            provider,
            options: new VoicePresenceModuleOptions
            {
                Priority = 42,
            });

        module.Priority.ShouldBe(42);

        await module.InitializeAsync(CancellationToken.None);
        await module.InitializeAsync(CancellationToken.None);

        provider.ConnectCalls.ShouldBe(0);
        provider.UpdateSessionCalls.ShouldBe(0);
    }

    [Theory]
    [MemberData(nameof(RealtimeProjectionProviderEvents))]
    public async Task Provider_control_events_should_enter_projection_backed_realtime_stream(
        VoiceProviderEvent providerEvent,
        VoiceRealtimeFrame.FrameOneofCase expectedFrameCase)
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var hub = new RecordingProjectionSessionEventHub();
        var services = new ServiceCollection()
            .AddSingleton<IProjectionSessionEventHub<VoiceRealtimeFrame>>(hub)
            .BuildServiceProvider();
        var ctx = new StubEventHandlerContext(services, CreateRoleAgentWithActiveSession());

        await module.HandleAsync(CreateEnvelope(providerEvent), ctx, CancellationToken.None);

        var published = hub.Events.ShouldHaveSingleItem();
        published.RootActorId.ShouldBe("voice-agent");
        published.SessionId.ShouldBe("session-1");
        published.Frame.FrameCase.ShouldBe(expectedFrameCase);
    }

    [Fact]
    public async Task Drain_acknowledged_should_not_publish_transcript_completed()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var hub = new RecordingProjectionSessionEventHub();
        var services = new ServiceCollection()
            .AddSingleton<IProjectionSessionEventHub<VoiceRealtimeFrame>>(hub)
            .BuildServiceProvider();
        var ctx = new StubEventHandlerContext(services, CreateRoleAgentWithActiveSession());
        RoleVoiceState(ctx).Status = VoicePresenceRuntimeStatus.AudioDraining;
        RoleVoiceState(ctx).CurrentResponseId = 5;

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            RemoteControlInputReceived = new VoiceRemoteControlInputReceived
            {
                SessionId = "session-1",
                ControlFrame = new VoiceControlFrame
                {
                    DrainAcknowledged = new VoiceDrainAcknowledged
                    {
                        ResponseId = 5,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        hub.Events.ShouldBeEmpty();
        var state = RoleVoiceState(ctx);
        state.Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        state.LastDrainAckResponseId.ShouldBe(5);
    }

    [Fact]
    public async Task Accepted_input_image_signal_should_forward_to_provider_without_state_or_realtime_publication()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var hub = new RecordingProjectionSessionEventHub();
        var services = new ServiceCollection()
            .AddSingleton<IProjectionSessionEventHub<VoiceRealtimeFrame>>(hub)
            .BuildServiceProvider();
        var ctx = new StubEventHandlerContext(services, CreateRoleAgentWithActiveSession());

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            InputImageReceived = new VoiceInputImageReceived
            {
                SessionId = "session-1",
                LeaseEpoch = 7,
                InputImage = new VoiceInputImage
                {
                    MediaType = "image/png",
                    Data = ByteString.CopyFrom([1, 2, 3]),
                },
            },
        }), ctx, CancellationToken.None);

        provider.InputImages.ShouldHaveSingleItem().Data.ToByteArray().ShouldBe([1, 2, 3]);
        ctx.Agent.ShouldBeOfType<RecordingRoleAgent>().PersistedStates.ShouldBeEmpty();
        hub.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Accepted_keyed_input_image_signal_should_forward_to_provider_without_state_or_realtime_publication()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var module = CreateModule(provider);
        var hub = new RecordingProjectionSessionEventHub();
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .AddSingleton<IProjectionSessionEventHub<VoiceRealtimeFrame>>(hub)
            .BuildServiceProvider();
        var roleAgent = CreateRoleAgentWithActiveSession();
        roleAgent.State.VoicePresence[DefaultModuleName].TransportAttached = true;
        roleAgent.State.VoicePresence[DefaultModuleName].ActiveTransportLeaseId = "transport-1";
        roleAgent.State.VoicePresence[DefaultModuleName].ActiveLeaseOwnerId = "host-1";
        roleAgent.State.VoicePresence[DefaultModuleName].LeaseExpiresAt =
            Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence[DefaultModuleName].LeaseEpoch = 3;
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            InputImageReceived = new VoiceInputImageReceived
            {
                SessionId = "session-1",
                OwnerId = "host-1",
                TransportLeaseId = "transport-1",
                LeaseExpiresAt = roleAgent.State.VoicePresence[DefaultModuleName].LeaseExpiresAt.Clone(),
                LeaseEpoch = 3,
                InputImage = new VoiceInputImage
                {
                    MediaType = "image/png",
                    Data = ByteString.CopyFrom([4, 5, 6]),
                },
            },
        }), ctx, CancellationToken.None);

        mediaPort.InputImages.ShouldHaveSingleItem();
        mediaPort.InputImages[0].TransportLeaseId.ShouldBe("transport-1");
        mediaPort.InputImages[0].InputImage.Data.ToByteArray().ShouldBe([4, 5, 6]);
        provider.InputImages.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
        roleAgent.PersistedStates.ShouldBeEmpty();
        hub.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stale_input_image_signal_should_be_ignored()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithActiveSession();
        roleAgent.State.VoicePresence[DefaultModuleName].TransportAttached = true;
        roleAgent.State.VoicePresence[DefaultModuleName].ActiveTransportLeaseId = "transport-1";
        roleAgent.State.VoicePresence[DefaultModuleName].ActiveLeaseOwnerId = "host-1";
        roleAgent.State.VoicePresence[DefaultModuleName].LeaseExpiresAt =
            Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence[DefaultModuleName].LeaseEpoch = 3;
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            InputImageReceived = new VoiceInputImageReceived
            {
                SessionId = "session-1",
                OwnerId = "host-1",
                TransportLeaseId = "stale-transport",
                LeaseExpiresAt = roleAgent.State.VoicePresence[DefaultModuleName].LeaseExpiresAt.Clone(),
                LeaseEpoch = 3,
                InputImage = new VoiceInputImage
                {
                    MediaType = "image/png",
                    Data = ByteString.CopyFrom([1]),
                },
            },
        }), ctx, CancellationToken.None);

        provider.InputImages.ShouldBeEmpty();
        roleAgent.PersistedStates.ShouldBeEmpty();
    }

    [Fact]
    public void CanHandle_should_accept_voice_frames_and_external_publications()
    {
        var module = CreateModule(new RecordingVoiceProvider());

        module.CanHandle(new EventEnvelope()).ShouldBeFalse();

        module.CanHandle(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        })).ShouldBeTrue();

        module.CanHandle(CreateEnvelope(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged { ResponseId = 1, PlayoutSequence = 7 },
        })).ShouldBeTrue();

        module.CanHandle(CreateEnvelope(new StringValue { Value = "external" })).ShouldBeTrue();

        module.CanHandle(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(new StringValue { Value = "direct" }),
            Route = EnvelopeRouteSemantics.CreateDirect("api", "voice-agent"),
        }).ShouldBeFalse();
    }

    [Fact]
    public async Task Speech_started_during_response_should_cancel_provider_and_switch_to_user_speaking()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }), ctx, CancellationToken.None);

        provider.CancelCalls.ShouldBe(1);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.UserSpeaking);
        RoleVoiceState(ctx).CurrentResponseId.ShouldBe(1);
    }

    [Fact]
    public async Task Speech_started_with_attached_lease_should_cancel_live_relay_without_provider_reconnect()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ProviderResponseId = "provider-r1",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    SpeechStarted = new VoiceSpeechStarted(),
                },
            },
        }), ctx, CancellationToken.None);

        mediaPort.CancelResponses.ShouldHaveSingleItem().ShouldBe("transport-current");
        provider.CancelCalls.ShouldBe(0);
        provider.ConnectCalls.ShouldBe(0);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.UserSpeaking);
    }

    [Fact]
    public async Task Speech_started_with_attached_lease_should_not_reconnect_when_live_relay_missing()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: false);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ProviderResponseId = "provider-r1",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    SpeechStarted = new VoiceSpeechStarted(),
                },
            },
        }), ctx, CancellationToken.None);

        mediaPort.CancelResponses.ShouldHaveSingleItem().ShouldBe("transport-current");
        provider.CancelCalls.ShouldBe(0);
        provider.ConnectCalls.ShouldBe(0);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.UserSpeaking);
    }

    [Fact]
    public async Task Response_done_and_drain_ack_should_release_injection_fence()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 2 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ResponseId = 2 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 2,
                PlayoutSequence = 88,
            },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        IsSafeToInject(RoleVoiceState(ctx)).ShouldBeTrue();
    }

    [Fact]
    public async Task Response_done_should_transition_to_audio_draining()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
    }

    [Fact]
    public async Task Response_done_on_active_lease_should_schedule_epoch_fenced_drain_timeout()
    {
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                DrainTimeout = TimeSpan.FromSeconds(7),
            });
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ProviderResponseId = "provider-r1",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseDone = new VoiceResponseDone
                    {
                        ProviderResponseId = "provider-r1",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        var scheduled = ctx.ScheduledTimeouts.ShouldHaveSingleItem();
        scheduled.CallbackId.ShouldBe("voice_presence:voice-drain-timeout:7:1");
        scheduled.DueTime.ShouldBe(TimeSpan.FromSeconds(7));
        var signal = scheduled.Event.ShouldBeOfType<VoiceModuleSignal>();
        signal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.DrainTimeoutExpired);
        signal.DrainTimeoutExpired.SessionId.ShouldBe("lease-current");
        signal.DrainTimeoutExpired.OwnerId.ShouldBe("host-current");
        signal.DrainTimeoutExpired.TransportLeaseId.ShouldBe("transport-current");
        signal.DrainTimeoutExpired.LeaseEpoch.ShouldBe(7);
        signal.DrainTimeoutExpired.ResponseId.ShouldBe(1);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
    }

    [Fact]
    public async Task Duplicate_response_done_while_audio_draining_should_not_schedule_another_timeout()
    {
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                DrainTimeout = TimeSpan.FromSeconds(7),
            });
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseDone = new VoiceResponseDone
                    {
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseDone = new VoiceResponseDone
                    {
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        var scheduled = ctx.ScheduledTimeouts.ShouldHaveSingleItem();
        scheduled.CallbackId.ShouldBe("voice_presence:voice-drain-timeout:7:1");
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Response_done_with_disabled_drain_timeout_should_remain_audio_draining_without_scheduling(
        int drainTimeoutSeconds)
    {
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                DrainTimeout = TimeSpan.FromSeconds(drainTimeoutSeconds),
            });
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseDone = new VoiceResponseDone
                    {
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        ctx.ScheduledTimeouts.ShouldBeEmpty();
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
    }

    [Fact]
    public async Task Drain_timeout_should_release_audio_draining_and_flush_pending_injection()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        roleAgent.State.VoicePresence[DefaultModuleName].Status = VoicePresenceRuntimeStatus.AudioDraining;
        roleAgent.State.VoicePresence[DefaultModuleName].CurrentResponseId = 4;
        roleAgent.State.VoicePresence[DefaultModuleName].NextResponseId = 5;
        roleAgent.State.VoicePresence[DefaultModuleName].PendingInjections.Add(new VoicePendingEventInjection
        {
            EnvelopeId = "external-timeout",
            PublisherActorId = "external-agent",
            EventType = StringValue.Descriptor.FullName,
            Payload = Any.Pack(new StringValue { Value = "safety event" }),
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            DrainTimeoutExpired = new VoiceDrainTimeoutExpired
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ResponseId = 4,
            },
        }), ctx, CancellationToken.None);

        mediaPort.EventInjections.ShouldHaveSingleItem();
        mediaPort.EventInjections[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.EventInjections[0].Injection.EnvelopeId.ShouldBe("external-timeout");
        provider.InjectedEvents.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
        var persisted = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        persisted.Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        persisted.LastDrainAckResponseId.ShouldBe(4);
        persisted.LastDrainAckPlayoutSequence.ShouldBe(-1);
        persisted.PendingInjections.ShouldBeEmpty();
        persisted.AwaitingInjectedResponseStart.ShouldBeTrue();
    }

    [Theory]
    [InlineData("response")]
    [InlineData("epoch")]
    [InlineData("zero-epoch")]
    [InlineData("transport")]
    public async Task Drain_timeout_should_ignore_stale_or_unfenced_signals(string mismatch)
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        roleAgent.State.VoicePresence[DefaultModuleName].Status = VoicePresenceRuntimeStatus.AudioDraining;
        roleAgent.State.VoicePresence[DefaultModuleName].CurrentResponseId = 4;
        roleAgent.State.VoicePresence[DefaultModuleName].NextResponseId = 5;
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            DrainTimeoutExpired = new VoiceDrainTimeoutExpired
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = mismatch == "transport" ? "transport-stale" : "transport-current",
                LeaseEpoch = mismatch switch
                {
                    "epoch" => 6,
                    "zero-epoch" => 0,
                    _ => 7,
                },
                ResponseId = mismatch == "response" ? 3 : 4,
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        var state = roleAgent.State.VoicePresence[DefaultModuleName];
        state.Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
        state.LastDrainAckResponseId.ShouldBe(-1);
    }

    [Fact]
    public async Task Drain_ack_after_timeout_should_be_idempotent_and_not_set_playout_sentinel()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        roleAgent.State.VoicePresence[DefaultModuleName].Status = VoicePresenceRuntimeStatus.AudioDraining;
        roleAgent.State.VoicePresence[DefaultModuleName].CurrentResponseId = 4;
        roleAgent.State.VoicePresence[DefaultModuleName].NextResponseId = 5;
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            DrainTimeoutExpired = new VoiceDrainTimeoutExpired
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ResponseId = 4,
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 4,
                PlayoutSequence = 99,
            },
        }), ctx, CancellationToken.None);

        var state = roleAgent.PersistedStates.Last().State;
        state.Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        state.LastDrainAckResponseId.ShouldBe(4);
        state.LastDrainAckPlayoutSequence.ShouldBe(-1);
    }

    [Fact]
    public async Task Drain_timeout_after_ack_should_be_idempotent_noop()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        roleAgent.State.VoicePresence[DefaultModuleName].Status = VoicePresenceRuntimeStatus.AudioDraining;
        roleAgent.State.VoicePresence[DefaultModuleName].CurrentResponseId = 4;
        roleAgent.State.VoicePresence[DefaultModuleName].NextResponseId = 5;
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 4,
                PlayoutSequence = 88,
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            DrainTimeoutExpired = new VoiceDrainTimeoutExpired
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ResponseId = 4,
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.Count.ShouldBe(1);
        var state = roleAgent.PersistedStates.Single().State;
        state.Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        state.LastDrainAckResponseId.ShouldBe(4);
        state.LastDrainAckPlayoutSequence.ShouldBe(88);
    }

    [Fact]
    public async Task Response_cancelled_should_return_to_idle()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
    }

    [Fact]
    public async Task Speech_stopped_should_not_change_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }), ctx, CancellationToken.None);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.UserSpeaking);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStopped = new VoiceSpeechStopped(),
        }), ctx, CancellationToken.None);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.UserSpeaking);
    }

    [Fact]
    public async Task Provider_disconnected_should_reset_to_idle()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Disconnected = new VoiceProviderDisconnected { Reason = "test" },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
    }

    [Fact]
    public async Task Noop_provider_events_should_not_change_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested { CallId = "c1", ToolName = "t", ArgumentsJson = "{}", ResponseId = 1 },
        }), ctx, CancellationToken.None);
        ctx.Agent.ShouldBeOfType<RecordingRoleAgent>().State.VoicePresence.ShouldBeEmpty();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Error = new VoiceProviderError { ErrorCode = "e", ErrorMessage = "msg" },
        }), ctx, CancellationToken.None);
        ctx.Agent.ShouldBeOfType<RecordingRoleAgent>().State.VoicePresence.ShouldBeEmpty();
    }

    [Fact]
    public async Task Function_call_should_execute_tool_and_send_result()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var module = CreateModule(provider, toolInvoker: invoker);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "call-1",
                ToolName = "doorbell.open",
                ArgumentsJson = """{"force":true}""",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        invoker.Calls.ShouldBe(1);
        invoker.LastCallId.ShouldBe("call-1");
        invoker.LastIssuedAtUnixMs.ShouldBeGreaterThan(0);
        invoker.LastToolName.ShouldBe("doorbell.open");
        invoker.LastArgumentsJson.ShouldBe("""{"force":true}""");
        provider.ToolResults.ShouldHaveSingleItem();
        provider.ToolResults[0].CallId.ShouldBe("call-1");
        provider.ToolResults[0].ResultJson.ShouldBe("""{"ok":true}""");
    }

    [Fact]
    public async Task Active_tool_context_should_flow_to_catalog_invoker_and_provider_session_key()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var catalog = new StaticVoiceToolCatalog([
            new VoiceToolDefinition
            {
                Name = "doorbell.open",
                Description = "open",
                ParametersSchema = "{}",
            },
        ]);
        var module = CreateModule(provider, toolInvoker: invoker, toolCatalog: catalog);
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = "voice-tool:ref-1",
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            CallerScopeId = "caller-scope-1",
        };
        var roleAgent = CreateRoleAgentWithActiveSession();
        roleAgent.State.VoicePresence[DefaultModuleName].ActiveToolContext = toolContext.Clone();
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "call-credential-ref",
                ToolName = "doorbell.open",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        invoker.LastToolContext.ShouldNotBeNull();
        invoker.LastToolContext.ShouldNotBeSameAs(toolContext);
        invoker.LastToolContext!.CredentialRef.ShouldBe("voice-tool:ref-1");
        invoker.LastOwnerActorId.ShouldBe(ctx.AgentId);
        invoker.LastSessionId.ShouldBe("session-1");
        catalog.LastToolContext.ShouldNotBeNull();
        catalog.LastToolContext!.CredentialRef.ShouldBe("voice-tool:ref-1");
        provider.LastSessionKey.ToolContext.ShouldNotBeNull();
        provider.LastSessionKey.ToolContext!.CredentialRef.ShouldBe("voice-tool:ref-1");
    }

    [Fact]
    public async Task Function_call_should_resolve_tool_invoker_from_services()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"service":true}""");
        var services = new ServiceCollection()
            .AddSingleton<IVoiceToolInvoker>(invoker)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext(services);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "call-2",
                ToolName = "doorbell.open",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        invoker.Calls.ShouldBe(1);
        provider.ToolResults[0].ResultJson.ShouldBe("""{"service":true}""");
    }

    [Fact]
    public async Task Function_call_should_send_result_to_live_relay_not_ephemeral_session()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"services":["home-assistant"]}""");
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var roleAgent = CreateRoleAgentWithActiveSession();
        roleAgent.State.VoicePresence[DefaultModuleName].ActiveTransportLeaseId = "transport-1";
        var module = CreateModule(provider, toolInvoker: invoker);
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "call-live",
                ToolName = "nyxid_status",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        // The result must land on the LIVE relay (the socket that emitted the call), not a throwaway session.
        mediaPort.ToolResults.ShouldHaveSingleItem();
        mediaPort.ToolResults[0].TransportLeaseId.ShouldBe("transport-1");
        mediaPort.ToolResults[0].CallId.ShouldBe("call-live");
        mediaPort.ToolResults[0].ResultJson.ShouldBe("""{"services":["home-assistant"]}""");
        provider.ToolResults.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Function_call_with_attached_lease_should_not_reconnect_when_live_relay_missing()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var mediaPort = new RecordingToolResultMediaPort(deliver: false);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var roleAgent = CreateRoleAgentWithActiveSession();
        roleAgent.State.VoicePresence[DefaultModuleName].ActiveTransportLeaseId = "transport-1";
        var module = CreateModule(provider, toolInvoker: invoker);
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "call-fallback",
                ToolName = "nyxid_status",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        mediaPort.ToolResults.ShouldHaveSingleItem();
        provider.ToolResults.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Function_call_timeout_should_send_error_result()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new BlockingVoiceToolInvoker();
        var module = CreateModule(
            provider,
            toolInvoker: invoker,
            options: new VoicePresenceModuleOptions
            {
                ToolExecutionTimeout = TimeSpan.FromMilliseconds(20),
            });
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "call-timeout",
                ToolName = "slow.tool",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        provider.ToolResults.ShouldHaveSingleItem();
        provider.ToolResults[0].ResultJson.ShouldContain("\"error\"");
        provider.ToolResults[0].ResultJson.ShouldContain("timed out");
    }

    [Fact]
    public async Task Client_owned_function_call_should_wait_for_typed_control_output()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"actor":true}""");
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var hub = new RecordingProjectionSessionEventHub();
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .AddSingleton<IProjectionSessionEventHub<VoiceRealtimeFrame>>(hub)
            .BuildServiceProvider();
        var module = CreateModule(
            provider,
            toolInvoker: invoker,
            toolCatalog: new StaticVoiceToolCatalog([
                new VoiceToolDefinition
                {
                    Name = "edge.light.toggle",
                    Description = "toggle local light",
                    ParametersSchema = "{}",
                    Owner = VoiceToolOwner.Client,
                },
            ]));
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    FunctionCall = new VoiceFunctionCallRequested
                    {
                        CallId = "client-call-1",
                        ToolName = "edge.light.toggle",
                        ArgumentsJson = """{"room":"entry"}""",
                        ProviderResponseId = "provider-r1",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        invoker.Calls.ShouldBe(0);
        provider.ToolResults.ShouldBeEmpty();
        var state = RoleVoiceState(ctx);
        var pendingCall = state.PendingClientToolCalls.ShouldHaveSingleItem();
        pendingCall.CallId.ShouldBe("client-call-1");
        pendingCall.ToolName.ShouldBe("edge.light.toggle");
        pendingCall.TransportLeaseId.ShouldBe("transport-current");
        pendingCall.LeaseEpoch.ShouldBe(7);
        var scheduled = ctx.ScheduledTimeouts.ShouldHaveSingleItem();
        scheduled.CallbackId.ShouldBe("voice_presence:voice-client-tool-timeout:7:client-call-1");
        var timeoutSignal = scheduled.Event.ShouldBeOfType<VoiceModuleSignal>();
        timeoutSignal.SignalCase.ShouldBe(VoiceModuleSignal.SignalOneofCase.ClientToolCallTimeoutExpired);
        timeoutSignal.ClientToolCallTimeoutExpired.CallId.ShouldBe("client-call-1");
        hub.Events.ShouldHaveSingleItem().Frame.FunctionCall.CallId.ShouldBe("client-call-1");

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    FunctionCallOutput = new VoiceFunctionCallOutput
                    {
                        CallId = "client-call-1",
                        ToolName = "edge.light.toggle",
                        OutputJson = """{"ok":true}""",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).PendingClientToolCalls.ShouldBeEmpty();
        mediaPort.ToolResults.ShouldHaveSingleItem();
        mediaPort.ToolResults[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.ToolResults[0].CallId.ShouldBe("client-call-1");
        mediaPort.ToolResults[0].ResultJson.ShouldBe("""{"ok":true}""");
        provider.ToolResults.ShouldBeEmpty();
    }

    [Fact]
    public async Task Client_tool_output_for_unknown_or_wrong_tool_should_be_ignored()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        roleAgent.State.VoicePresence[DefaultModuleName].PendingClientToolCalls.Add(new VoicePendingClientToolCall
        {
            CallId = "client-call-1",
            ToolName = "edge.light.toggle",
            SessionId = "lease-current",
            OwnerId = "host-current",
            TransportLeaseId = "transport-current",
            LeaseEpoch = 7,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(10)),
        });
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    FunctionCallOutput = new VoiceFunctionCallOutput
                    {
                        CallId = "client-call-1",
                        ToolName = "edge.other",
                        OutputJson = """{"ok":true}""",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).PendingClientToolCalls.ShouldHaveSingleItem().CallId.ShouldBe("client-call-1");
        mediaPort.ToolResults.ShouldBeEmpty();
        provider.ToolResults.ShouldBeEmpty();
        roleAgent.PersistedStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Client_tool_output_without_valid_transport_fence_should_be_ignored()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        roleAgent.State.VoicePresence[DefaultModuleName].PendingClientToolCalls.Add(new VoicePendingClientToolCall
        {
            CallId = "client-call-1",
            ToolName = "edge.light.toggle",
            SessionId = "lease-current",
            OwnerId = "host-current",
            TransportLeaseId = "transport-current",
            LeaseEpoch = 7,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(10)),
        });
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame
        {
            FunctionCallOutput = new VoiceFunctionCallOutput
            {
                CallId = "client-call-1",
                ToolName = "edge.light.toggle",
                OutputJson = """{"ok":true}""",
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-other",
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    FunctionCallOutput = new VoiceFunctionCallOutput
                    {
                        CallId = "client-call-1",
                        ToolName = "edge.light.toggle",
                        OutputJson = """{"ok":true}""",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).PendingClientToolCalls.ShouldHaveSingleItem().CallId.ShouldBe("client-call-1");
        mediaPort.ToolResults.ShouldBeEmpty();
        provider.ToolResults.ShouldBeEmpty();
        roleAgent.PersistedStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Client_tool_failure_output_should_clear_pending_call_and_deliver_error_json()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        AddPendingClientToolCall(roleAgent, "client-call-failed");
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    FunctionCallOutput = new VoiceFunctionCallOutput
                    {
                        CallId = "client-call-failed",
                        ToolName = "edge.light.toggle",
                        Failure = new VoiceFunctionCallFailure
                        {
                            ErrorCode = "edge_failed",
                            ErrorMessage = "edge tool failed",
                        },
                    },
                },
            },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).PendingClientToolCalls.ShouldBeEmpty();
        mediaPort.ToolResults.ShouldHaveSingleItem();
        mediaPort.ToolResults[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.ToolResults[0].CallId.ShouldBe("client-call-failed");
        mediaPort.ToolResults[0].ResultJson.ShouldContain("edge_failed");
        mediaPort.ToolResults[0].ResultJson.ShouldContain("edge tool failed");
        provider.ToolResults.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Client_tool_invalid_output_should_clear_pending_call_and_deliver_error_json(bool emptyOutputJson)
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        AddPendingClientToolCall(roleAgent, "client-call-invalid");
        var ctx = new StubEventHandlerContext(services, roleAgent);
        var output = new VoiceFunctionCallOutput
        {
            CallId = "client-call-invalid",
            ToolName = "edge.light.toggle",
        };
        if (emptyOutputJson)
            output.OutputJson = string.Empty;

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    FunctionCallOutput = output,
                },
            },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).PendingClientToolCalls.ShouldBeEmpty();
        mediaPort.ToolResults.ShouldHaveSingleItem();
        mediaPort.ToolResults[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.ToolResults[0].CallId.ShouldBe("client-call-invalid");
        mediaPort.ToolResults[0].ResultJson.ShouldContain("invalid_client_tool_output");
        mediaPort.ToolResults[0].ResultJson.ShouldContain("client tool output did not include a result");
        provider.ToolResults.ShouldBeEmpty();
    }

    [Fact]
    public async Task Client_tool_timeout_should_resume_actor_and_send_error_result()
    {
        var now = new DateTimeOffset(2026, 6, 18, 8, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(
            provider,
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = timeProvider,
                ToolExecutionTimeout = TimeSpan.FromSeconds(3),
            });
        var roleAgent = CreateRoleAgentWithAttachedTransport(now.AddMinutes(5));
        roleAgent.State.VoicePresence[DefaultModuleName].PendingClientToolCalls.Add(new VoicePendingClientToolCall
        {
            CallId = "client-call-timeout",
            ToolName = "edge.light.toggle",
            SessionId = "lease-current",
            OwnerId = "host-current",
            TransportLeaseId = "transport-current",
            LeaseEpoch = 7,
            ExpiresAt = Timestamp.FromDateTimeOffset(now.AddSeconds(3)),
        });
        var ctx = new StubEventHandlerContext(services, roleAgent);
        timeProvider.Advance(TimeSpan.FromSeconds(3));

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            ClientToolCallTimeoutExpired = new VoiceClientToolCallTimeoutExpired
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                CallId = "client-call-timeout",
                ToolName = "edge.light.toggle",
            },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).PendingClientToolCalls.ShouldBeEmpty();
        mediaPort.ToolResults.ShouldHaveSingleItem();
        mediaPort.ToolResults[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.ToolResults[0].CallId.ShouldBe("client-call-timeout");
        mediaPort.ToolResults[0].ResultJson.ShouldContain("client_tool_timeout");
        mediaPort.ToolResults[0].ResultJson.ShouldContain("timed out after 3000 ms");
        provider.ToolResults.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Module_signal_should_ignore_events_for_other_voice_module_aliases()
    {
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                Name = "voice_presence_openai",
            });
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence_minicpm",
            ProviderEvent = new VoiceProviderEvent
            {
                ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
            },
        }), ctx, CancellationToken.None);

        ctx.Agent.ShouldBeOfType<RecordingRoleAgent>().State.VoicePresence.ShouldBeEmpty();
    }

    [Fact]
    public async Task Provider_response_identity_should_be_mapped_by_module_turn()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var module = CreateModule(provider, toolInvoker: invoker);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                ProviderResponseId = "provider-r1",
                CallId = "call-1",
                ToolName = "doorbell.open",
                ArgumentsJson = "{}",
            },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).CurrentResponseId.ShouldBe(1);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
        invoker.Calls.ShouldBe(1);
        provider.ToolResults.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Provider_response_cancellation_should_use_module_mapped_response_id_and_retire_mapping()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).CurrentResponseId.ShouldBe(1);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r2" },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).CurrentResponseId.ShouldBe(2);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.ResponseInProgress);
    }

    [Fact]
    public async Task Speech_started_should_cancel_active_provider_response_inside_module_turn()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        provider.CancelCalls.ShouldBe(1);
        RoleVoiceState(ctx).CurrentResponseId.ShouldBe(1);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.UserSpeaking);
    }

    [Fact]
    public void Provider_adapters_should_not_own_response_epoch_state()
    {
        var repoRoot = FindRepositoryRoot();
        var providerSources = new[]
        {
            Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence.OpenAI/OpenAIRealtimeProvider.cs"),
            Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence.MiniCPM/MiniCPMRealtimeProvider.cs"),
        };
        var forbiddenTokens = new[]
        {
            "_responseEpochs",
            "_nextResponseId",
            "_activeResponseId",
            "_suppressedResponseId",
            "Interlocked.Increment",
        };

        foreach (var sourcePath in providerSources)
        {
            File.Exists(sourcePath).ShouldBeTrue(sourcePath);
            var source = StripLineComments(File.ReadAllLines(sourcePath));
            foreach (var token in forbiddenTokens)
                source.ShouldNotContain(token, Case.Sensitive, $"{Path.GetFileName(sourcePath)} must emit provider-native ids only");
        }

        var moduleSourcePath = Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Modules/VoicePresenceModule.cs");
        var moduleSource = File.ReadAllText(moduleSourcePath);
        moduleSource.ShouldContain("VoicePresenceRuntimeState");
        moduleSource.ShouldNotContain("private readonly Dictionary<string, int> _providerResponseIds");
    }

    [Fact]
    public void Voice_presence_module_should_not_keep_runtime_fact_fields()
    {
        var repoRoot = FindRepositoryRoot();
        var moduleSourcePath = Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Modules/VoicePresenceModule.cs");
        var moduleSource = StripLineComments(File.ReadAllLines(moduleSourcePath));

        moduleSource.ShouldNotContain("VoicePresenceRuntimeState _runtimeState", Case.Sensitive);
        moduleSource.ShouldNotContain("new VoicePresenceRuntimeState", Case.Sensitive);
        moduleSource.ShouldNotContain("IVoiceTransport? _userTransport", Case.Sensitive);
        moduleSource.ShouldNotContain("CancellationTokenSource? _relayCts", Case.Sensitive);
        moduleSource.ShouldNotContain("Task? _userToProviderRelay", Case.Sensitive);
        moduleSource.ShouldNotContain("Task? _providerToUserRelay", Case.Sensitive);
        moduleSource.ShouldNotContain("TransportAttached = _transportLease", Case.Sensitive);
        moduleSource.ShouldNotContain("TransportAttached = _userTransport", Case.Sensitive);
        moduleSource.ShouldNotContain("IsActorAccepted", Case.Sensitive);
        moduleSource.ShouldNotContain("DispatchFireAndForget", Case.Sensitive);
        moduleSource.ShouldNotContain("VoiceTransportLease _", Case.Sensitive);
        moduleSource.ShouldNotContain("VoiceTransportLease(", Case.Sensitive);
        moduleSource.ShouldNotContain("VoiceTransportLease =", Case.Sensitive);
        moduleSource.ShouldNotContain("_transportPump", Case.Sensitive);
        moduleSource.ShouldNotContain("_providerSession", Case.Sensitive);
        moduleSource.ShouldNotContain("_providerSessionKey", Case.Sensitive);
        moduleSource.ShouldNotContain("_volatileSelfSignalDispatcher", Case.Sensitive);
        moduleSource.ShouldNotContain("VoiceTransportRelayKey", Case.Sensitive);
        moduleSource.ShouldNotContain("VoiceTransportRelayPump", Case.Sensitive);
        moduleSource.ShouldNotContain("AttachTransport(", Case.Sensitive);
        moduleSource.ShouldNotContain("AttachTransportAsync(", Case.Sensitive);
        moduleSource.ShouldNotContain("RunUserToProviderRelayAsync", Case.Sensitive);
    }

    [Fact]
    public void Voice_presence_should_not_restore_public_audio_fast_path_bypass()
    {
        var repoRoot = FindRepositoryRoot();
        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence.Abstractions/IAudioFastPath.cs"))
            .ShouldBeFalse();
        File.Exists(Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence.Abstractions/VoiceAudioFastPathFrame.cs"))
            .ShouldBeFalse();

        var moduleSourcePath = Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence/Modules/VoicePresenceModule.cs");
        var moduleSource = StripLineComments(File.ReadAllLines(moduleSourcePath));
        moduleSource.ShouldNotContain("IAudioFastPath", Case.Sensitive);
        moduleSource.ShouldNotContain("CanHandleAudio", Case.Sensitive);
        moduleSource.ShouldNotContain("HandleAudioAsync", Case.Sensitive);
    }

    [Fact]
    public void Voice_provider_must_not_reintroduce_legacy_OnEvent_callback_or_session_shim()
    {
        var repoRoot = FindRepositoryRoot();
        var providerInterface = File.ReadAllText(Path.Combine(
            repoRoot,
            "src/Aevatar.Foundation.VoicePresence.Abstractions/IRealtimeVoiceProvider.cs"));
        providerInterface.ShouldNotContain(
            "OnEvent",
            Case.Sensitive,
            "legacy mutable callback deleted per iter106/cluster-106 - typed event sink only");
        providerInterface.ShouldNotContain(
            "LegacyRealtimeVoiceProviderSession",
            Case.Sensitive,
            "legacy session shim deleted per Phase 8 r1 reject");

        var providerSources = new[]
        {
            Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence.OpenAI/OpenAIRealtimeProvider.cs"),
            Path.Combine(repoRoot, "src/Aevatar.Foundation.VoicePresence.MiniCPM/MiniCPMRealtimeProvider.cs"),
        };

        foreach (var sourcePath in providerSources)
        {
            var source = File.ReadAllText(sourcePath);
            source.ShouldNotContain(
                "OnEvent",
                Case.Sensitive,
                $"{Path.GetFileName(sourcePath)} must keep provider callbacks on typed event sinks");
        }
    }

    [Fact]
    public async Task Provider_response_identity_should_persist_in_role_gagent_voice_sub_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldHaveSingleItem();
        var runtimeState = roleAgent.State.VoicePresence["voice_presence"];
        runtimeState.ProviderResponseBindings.ShouldHaveSingleItem();
        runtimeState.ProviderResponseBindings[0].ProviderResponseId.ShouldBe("provider-r1");
        runtimeState.ProviderResponseBindings[0].ResponseId.ShouldBe(1);
        runtimeState.Status.ShouldBe(VoicePresenceRuntimeStatus.ResponseInProgress);
    }

    [Fact]
    public async Task Session_lease_signals_should_persist_actor_owned_capability_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(agent: roleAgent);
        var toolContext = new VoiceToolExecutionContext
        {
            CredentialRef = "voice-tool:lease-ref",
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            CallerScopeId = "caller-scope-lease",
        };

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            SessionLeaseRequested = new VoicePresenceSessionLeaseRequested
            {
                SessionId = "lease-1",
                OwnerId = "host-1",
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
                ToolContext = toolContext.Clone(),
            },
        }), ctx, CancellationToken.None);

        var leasedState = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        leasedState.ActiveSessionId.ShouldBe("lease-1");
        leasedState.ActiveLeaseOwnerId.ShouldBe("host-1");
        leasedState.Initialized.ShouldBeTrue();
        leasedState.TransportAttached.ShouldBeFalse();
        leasedState.PcmSampleRateHz.ShouldBe(24000);
        leasedState.RemoteAudioSupport.ShouldBe(VoiceRemoteAudioSupport.LocalOnly);
        leasedState.ActiveToolContext.ShouldNotBeSameAs(toolContext);
        leasedState.ActiveToolContext.CredentialRef.ShouldBe("voice-tool:lease-ref");

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            SessionLeaseReleased = new VoicePresenceSessionLeaseReleased
            {
                SessionId = "lease-1",
                Reason = "test-release",
            },
        }), ctx, CancellationToken.None);

        var releasedState = roleAgent.PersistedStates.Last().State;
        releasedState.ActiveSessionId.ShouldBeEmpty();
        releasedState.ActiveLeaseOwnerId.ShouldBeEmpty();
        releasedState.LeaseExpiresAt.ShouldBeNull();
        releasedState.ActiveToolContext.ShouldBeNull();
    }

    [Fact]
    public async Task Transport_attach_signal_should_persist_actor_owned_transport_attachment()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            ActiveLeaseOwnerId = "host-1",
            LeaseExpiresAt = expiresAt.Clone(),
            Initialized = true,
            LeaseEpoch = 7,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportAttachRequested = new VoiceTransportAttachRequested
            {
                SessionId = "lease-1",
                OwnerId = "host-1",
                TransportLeaseId = "transport-1",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = 7,
            },
        }), ctx, CancellationToken.None);

        var attached = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        attached.TransportAttached.ShouldBeTrue();
        attached.ActiveTransportLeaseId.ShouldBe("transport-1");
        attached.ActiveLeaseOwnerId.ShouldBe("host-1");
        attached.ActiveSessionId.ShouldBe("lease-1");
        provider.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Transport_attach_signal_should_reject_mismatched_owner_or_expired_lease()
    {
        var now = new DateTimeOffset(2026, 5, 23, 9, 0, 0, TimeSpan.Zero);
        var timeProvider = new ManualTimeProvider(now);
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = timeProvider,
            });
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var activeExpiry = Timestamp.FromDateTimeOffset(now.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            ActiveLeaseOwnerId = "host-1",
            LeaseExpiresAt = activeExpiry.Clone(),
            Initialized = true,
            LeaseEpoch = 7,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportAttachRequested = new VoiceTransportAttachRequested
            {
                SessionId = "lease-1",
                OwnerId = "host-2",
                TransportLeaseId = "transport-1",
                LeaseExpiresAt = activeExpiry.Clone(),
                LeaseEpoch = 7,
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();

        roleAgent.State.VoicePresence["voice_presence"].ActiveLeaseOwnerId = "host-1";
        roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt =
            Timestamp.FromDateTimeOffset(now.AddSeconds(-1));

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportAttachRequested = new VoiceTransportAttachRequested
            {
                SessionId = "lease-1",
                OwnerId = "host-1",
                TransportLeaseId = "transport-1",
                LeaseExpiresAt = Timestamp.FromDateTimeOffset(now.AddSeconds(-1)),
                LeaseEpoch = 7,
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        roleAgent.State.VoicePresence["voice_presence"].TransportAttached.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task Transport_attach_signal_should_reject_zero_or_stale_lease_epoch(long leaseEpoch)
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            ActiveLeaseOwnerId = "host-1",
            LeaseExpiresAt = expiresAt.Clone(),
            Initialized = true,
            LeaseEpoch = 7,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportAttachRequested = new VoiceTransportAttachRequested
            {
                SessionId = "lease-1",
                OwnerId = "host-1",
                TransportLeaseId = "transport-1",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = leaseEpoch,
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        roleAgent.State.VoicePresence["voice_presence"].TransportAttached.ShouldBeFalse();
    }

    [Fact]
    public async Task Transport_lease_renew_signal_should_extend_actor_owned_expiry_without_bumping_epoch()
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = new ManualTimeProvider(now),
            });
        var roleAgent = CreateRoleAgentWithAttachedTransport(now.AddMinutes(5));
        var ctx = new StubEventHandlerContext(agent: roleAgent);
        var renewExpiresAt = Timestamp.FromDateTimeOffset(now.AddMinutes(10));

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLeaseRenewRequested = new VoiceTransportLeaseRenewRequested
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                RenewExpiresAt = renewExpiresAt.Clone(),
            },
        }), ctx, CancellationToken.None);

        var renewed = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        renewed.LeaseExpiresAt.ShouldBe(renewExpiresAt);
        renewed.LeaseEpoch.ShouldBe(7);
        renewed.ActiveTransportLeaseId.ShouldBe("transport-current");
        renewed.TransportAttached.ShouldBeTrue();
    }

    [Fact]
    public async Task Transport_lease_renew_signal_should_not_shorten_actor_owned_expiry()
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = new ManualTimeProvider(now),
            });
        var roleAgent = CreateRoleAgentWithAttachedTransport(now.AddMinutes(10));
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLeaseRenewRequested = new VoiceTransportLeaseRenewRequested
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                RenewExpiresAt = Timestamp.FromDateTimeOffset(now.AddMinutes(5)),
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.ToDateTimeOffset()
            .ShouldBe(now.AddMinutes(10));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("transport")]
    [InlineData("epoch")]
    [InlineData("expired")]
    public async Task Transport_lease_renew_signal_should_ignore_identity_mismatch_or_expired_lease(
        string mismatch)
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = new ManualTimeProvider(now),
            });
        var activeExpiry = mismatch == "expired" ? now.AddSeconds(-1) : now.AddMinutes(5);
        var roleAgent = CreateRoleAgentWithAttachedTransport(activeExpiry);
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLeaseRenewRequested = new VoiceTransportLeaseRenewRequested
            {
                SessionId = "lease-current",
                OwnerId = mismatch == "owner" ? "host-stale" : "host-current",
                TransportLeaseId = mismatch == "transport" ? "transport-stale" : "transport-current",
                LeaseEpoch = mismatch == "epoch" ? 6 : 7,
                RenewExpiresAt = Timestamp.FromDateTimeOffset(now.AddMinutes(10)),
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.ToDateTimeOffset()
            .ShouldBe(activeExpiry);
    }

    [Fact]
    public async Task Transport_control_signal_should_accept_old_lease_expiry_after_renewal()
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var oldExpiry = Timestamp.FromDateTimeOffset(now.AddMinutes(5));
        var roleAgent = CreateRoleAgentWithAttachedTransport(now.AddMinutes(10));
        var state = roleAgent.State.VoicePresence["voice_presence"];
        state.Status = VoicePresenceRuntimeStatus.AudioDraining;
        state.CurrentResponseId = 4;
        state.NextResponseId = 5;
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = new ManualTimeProvider(now),
            });
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = oldExpiry.Clone(),
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    DrainAcknowledged = new VoiceDrainAcknowledged
                    {
                        ResponseId = 4,
                        PlayoutSequence = 9,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        var persisted = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        persisted.LastDrainAckResponseId.ShouldBe(4);
        persisted.LastDrainAckPlayoutSequence.ShouldBe(9);
        persisted.LeaseExpiresAt.ToDateTimeOffset().ShouldBe(now.AddMinutes(10));
    }

    [Fact]
    public async Task Provider_callback_signal_should_accept_old_lease_expiry_after_renewal()
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var oldExpiry = Timestamp.FromDateTimeOffset(now.AddMinutes(5));
        var roleAgent = CreateRoleAgentWithAttachedTransport(now.AddMinutes(10));
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = new ManualTimeProvider(now),
            });
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = oldExpiry.Clone(),
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
                },
            },
        }), ctx, CancellationToken.None);

        var persisted = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        persisted.Status.ShouldBe(VoicePresenceRuntimeStatus.ResponseInProgress);
        persisted.ProviderResponseBindings.ShouldHaveSingleItem().ProviderResponseId.ShouldBe("provider-r1");
        persisted.LeaseExpiresAt.ToDateTimeOffset().ShouldBe(now.AddMinutes(10));
    }

    [Fact]
    public async Task Input_image_signal_should_accept_old_lease_expiry_after_renewal()
    {
        var now = new DateTimeOffset(2026, 6, 17, 8, 0, 0, TimeSpan.Zero);
        var oldExpiry = Timestamp.FromDateTimeOffset(now.AddMinutes(5));
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var roleAgent = CreateRoleAgentWithAttachedTransport(now.AddMinutes(10));
        var module = CreateModule(
            provider,
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = new ManualTimeProvider(now),
            });
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            InputImageReceived = new VoiceInputImageReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = oldExpiry.Clone(),
                LeaseEpoch = 7,
                InputImage = new VoiceInputImage
                {
                    MediaType = "image/png",
                    Data = ByteString.CopyFrom([7, 8, 9]),
                },
            },
        }), ctx, CancellationToken.None);

        mediaPort.InputImages.ShouldHaveSingleItem();
        mediaPort.InputImages[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.InputImages[0].InputImage.Data.ToByteArray().ShouldBe([7, 8, 9]);
        provider.InputImages.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
        roleAgent.PersistedStates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stale_transport_signal_should_not_mutate_actor_owned_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
            TransportAttached = true,
            LeaseEpoch = 7,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.AudioDraining,
            CurrentResponseId = 4,
            NextResponseId = 5,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-old",
                OwnerId = "host-current",
                TransportLeaseId = "transport-old",
                LeaseExpiresAt = roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.Clone(),
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    DrainAcknowledged = new VoiceDrainAcknowledged
                    {
                        ResponseId = 4,
                        PlayoutSequence = 9,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        roleAgent.State.VoicePresence["voice_presence"].LastDrainAckResponseId.ShouldBe(-1);
    }

    [Fact]
    public async Task Expired_transport_signal_should_not_mutate_actor_owned_state()
    {
        var now = new DateTimeOffset(2026, 5, 23, 9, 0, 0, TimeSpan.Zero);
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                TimeProvider = new ManualTimeProvider(now),
            });
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = Timestamp.FromDateTimeOffset(now.AddSeconds(-1)),
            TransportAttached = true,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.AudioDraining,
            CurrentResponseId = 4,
            NextResponseId = 5,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportControlFrameReceived = new VoiceTransportControlFrameReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.Clone(),
                LeaseEpoch = 7,
                ControlFrame = new VoiceControlFrame
                {
                    DrainAcknowledged = new VoiceDrainAcknowledged
                    {
                        ResponseId = 4,
                        PlayoutSequence = 9,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        roleAgent.State.VoicePresence["voice_presence"].LastDrainAckResponseId.ShouldBe(-1);
    }

    [Fact]
    public async Task Stale_provider_callback_signal_should_not_mutate_actor_owned_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = expiresAt.Clone(),
            TransportAttached = true,
            LeaseEpoch = 7,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-old",
                OwnerId = "host-current",
                TransportLeaseId = "transport-old",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
                },
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        var state = roleAgent.State.VoicePresence["voice_presence"];
        state.Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        state.ProviderResponseBindings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unkeyed_provider_callback_signal_should_not_mutate_actor_owned_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "remote-current",
            RemoteSessionId = "remote-current",
            LeaseEpoch = 7,
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
                },
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        roleAgent.State.VoicePresence["voice_presence"].ProviderResponseBindings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Accepted_provider_callback_signal_should_mutate_actor_owned_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = expiresAt.Clone(),
            TransportAttached = true,
            LeaseEpoch = 7,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
                },
            },
        }), ctx, CancellationToken.None);

        var state = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        state.Status.ShouldBe(VoicePresenceRuntimeStatus.ResponseInProgress);
        state.ProviderResponseBindings.ShouldHaveSingleItem().ProviderResponseId.ShouldBe("provider-r1");
    }

    [Fact]
    public async Task Remote_provider_callback_signal_should_mutate_matching_remote_session_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "remote-current",
            RemoteSessionId = "remote-current",
            LeaseEpoch = 7,
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "remote-current",
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
                },
            },
        }), ctx, CancellationToken.None);

        var state = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        state.Status.ShouldBe(VoicePresenceRuntimeStatus.ResponseInProgress);
        state.ProviderResponseBindings.ShouldHaveSingleItem().ProviderResponseId.ShouldBe("provider-r1");
    }

    [Fact]
    public async Task Transport_detach_signal_should_clear_actor_owned_transport_attachment()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = expiresAt.Clone(),
            TransportAttached = true,
            LeaseEpoch = 7,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportDetachRequested = new VoiceTransportDetachRequested
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = 7,
                Reason = "test-detach",
            },
        }), ctx, CancellationToken.None);

        var state = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        state.TransportAttached.ShouldBeFalse();
        state.ActiveTransportLeaseId.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transport_relay_stopped_signal_should_clear_actor_owned_transport_attachment()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = expiresAt.Clone(),
            TransportAttached = true,
            LeaseEpoch = 7,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportRelayStopped = new VoiceTransportRelayStopped
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = 7,
                Reason = "test-stop",
            },
        }), ctx, CancellationToken.None);

        var state = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        state.TransportAttached.ShouldBeFalse();
        state.ActiveTransportLeaseId.ShouldBeEmpty();
    }

    [Fact]
    public async Task Transport_lifetime_completed_signal_should_release_active_actor_owned_session()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = expiresAt.Clone(),
            TransportAttached = true,
            LeaseEpoch = 7,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLifetimeCompleted = new VoiceTransportLifetimeCompleted
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = 7,
                Reason = "host_transport_completed",
            },
        }), ctx, CancellationToken.None);

        var state = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        state.TransportAttached.ShouldBeFalse();
        state.ActiveTransportLeaseId.ShouldBeEmpty();
        state.ActiveSessionId.ShouldBeEmpty();
        state.ActiveLeaseOwnerId.ShouldBeEmpty();
        state.LeaseExpiresAt.ShouldBeNull();
    }

    [Fact]
    public async Task Stale_transport_lifetime_completed_signal_should_not_release_active_actor_owned_session()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var expiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = expiresAt.Clone(),
            TransportAttached = true,
            LeaseEpoch = 7,
            ActiveTransportLeaseId = "transport-current",
            Status = VoicePresenceRuntimeStatus.Idle,
            CurrentResponseId = 0,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            TransportLifetimeCompleted = new VoiceTransportLifetimeCompleted
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-stale",
                LeaseExpiresAt = expiresAt.Clone(),
                LeaseEpoch = 7,
                Reason = "host_transport_completed",
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        var state = roleAgent.State.VoicePresence["voice_presence"];
        state.TransportAttached.ShouldBeTrue();
        state.ActiveTransportLeaseId.ShouldBe("transport-current");
        state.ActiveSessionId.ShouldBe("lease-current");
    }

    [Fact]
    public async Task Provider_callback_with_stale_lease_epoch_should_be_rejected()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(agent: roleAgent);
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            TransportAttached = true,
            ActiveTransportLeaseId = "transport-current",
            LeaseEpoch = 12,
            Status = VoicePresenceRuntimeStatus.Idle,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 11,
                ProviderEvent = new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ProviderResponseId = "stale-response",
                    },
                },
            },
        }), ctx, CancellationToken.None);

        roleAgent.State.VoicePresence["voice_presence"].CurrentResponseId.ShouldBe(0);
        provider.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Provider_function_call_callback_with_zero_lease_epoch_should_not_execute_tool()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider, toolInvoker: invoker);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.Clone(),
                LeaseEpoch = 0,
                ProviderEvent = new VoiceProviderEvent
                {
                    FunctionCall = new VoiceFunctionCallRequested
                    {
                        CallId = "call-zero",
                        ToolName = "doorbell.open",
                        ArgumentsJson = "{}",
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        invoker.Calls.ShouldBe(0);
        provider.ToolResults.ShouldBeEmpty();
    }

    [Fact]
    public async Task Provider_function_call_callback_with_active_lease_epoch_should_execute_tool()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider, toolInvoker: invoker);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.Clone(),
                LeaseEpoch = 7,
                ProviderEvent = new VoiceProviderEvent
                {
                    FunctionCall = new VoiceFunctionCallRequested
                    {
                        CallId = "call-active",
                        ToolName = "doorbell.open",
                        ArgumentsJson = "{}",
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        invoker.Calls.ShouldBe(1);
        mediaPort.ToolResults.ShouldHaveSingleItem();
        mediaPort.ToolResults[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.ToolResults[0].CallId.ShouldBe("call-active");
        provider.ToolResults.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Provider_function_call_callback_with_stale_lease_epoch_should_not_execute_tool()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var module = CreateModule(provider, toolInvoker: invoker);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            ProviderEventReceived = new VoiceProviderEventReceived
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseExpiresAt = roleAgent.State.VoicePresence["voice_presence"].LeaseExpiresAt.Clone(),
                LeaseEpoch = 6,
                ProviderEvent = new VoiceProviderEvent
                {
                    FunctionCall = new VoiceFunctionCallRequested
                    {
                        CallId = "call-stale",
                        ToolName = "doorbell.open",
                        ArgumentsJson = "{}",
                        ResponseId = 1,
                    },
                },
            },
        }), ctx, CancellationToken.None);

        invoker.Calls.ShouldBe(0);
        provider.ToolResults.ShouldBeEmpty();
    }

    [Fact]
    public async Task Conflicting_session_lease_request_should_keep_existing_active_session()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var firstExpiry = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            LeaseExpiresAt = firstExpiry.Clone(),
            Initialized = true,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            SessionLeaseRequested = new VoicePresenceSessionLeaseRequested
            {
                SessionId = "lease-2",
                OwnerId = "host-2",
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        var storedState = roleAgent.State.VoicePresence["voice_presence"];
        storedState.ActiveSessionId.ShouldBe("lease-1");
        storedState.LeaseExpiresAt.ShouldBe(firstExpiry);
    }

    [Fact]
    public async Task Stale_session_lease_release_should_not_clear_active_session()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var activeExpiry = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5));
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            LeaseExpiresAt = activeExpiry.Clone(),
            Initialized = true,
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            SessionLeaseReleased = new VoicePresenceSessionLeaseReleased
            {
                SessionId = "lease-2",
                Reason = "stale-release",
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldBeEmpty();
        var storedState = roleAgent.State.VoicePresence["voice_presence"];
        storedState.ActiveSessionId.ShouldBe("lease-1");
        storedState.LeaseExpiresAt.ShouldBe(activeExpiry);
    }

    [Fact]
    public async Task Fresh_module_should_hydrate_provider_response_binding_from_role_gagent_voice_sub_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            Status = VoicePresenceRuntimeStatus.ResponseInProgress,
            CurrentResponseId = 7,
            NextResponseId = 8,
            ActiveProviderResponseId = "provider-r1",
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
            ProviderResponseBindings =
            {
                new VoiceProviderResponseBinding
                {
                    ProviderResponseId = "provider-r1",
                    ResponseId = 7,
                },
            },
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        RoleVoiceState(ctx).CurrentResponseId.ShouldBe(7);
        RoleVoiceState(ctx).Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
        var persistedState = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        persistedState.ProviderResponseBindings.ShouldBeEmpty();
        persistedState.CurrentResponseId.ShouldBe(7);
        persistedState.NextResponseId.ShouldBe(8);
        persistedState.Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
    }

    [Fact]
    public async Task Fresh_module_should_not_lose_voice_authority_state_after_module_restart()
    {
        var firstModule = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await firstModule.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        var persistedByFirstModule = roleAgent.State.VoicePresence[DefaultModuleName].Clone();
        persistedByFirstModule.Status.ShouldBe(VoicePresenceRuntimeStatus.ResponseInProgress);
        persistedByFirstModule.CurrentResponseId.ShouldBe(1);

        await firstModule.DisposeAsync();
        var restartedModule = CreateModule(new RecordingVoiceProvider());

        await restartedModule.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        var persistedByRestartedModule = roleAgent.State.VoicePresence[DefaultModuleName];
        persistedByRestartedModule.CurrentResponseId.ShouldBe(1);
        persistedByRestartedModule.Status.ShouldBe(VoicePresenceRuntimeStatus.AudioDraining);
        persistedByRestartedModule.ProviderResponseBindings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Fresh_module_should_hydrate_pending_injection_and_persist_awaiting_fence_after_drain_ack()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            Status = VoicePresenceRuntimeStatus.AudioDraining,
            CurrentResponseId = 4,
            NextResponseId = 5,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
            PendingInjections =
            {
                new VoicePendingEventInjection
                {
                    EnvelopeId = "external-1",
                    PublisherActorId = "external-agent",
                    EventType = StringValue.Descriptor.FullName,
                    Payload = Any.Pack(new StringValue { Value = "door opened" }),
                    ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            },
        };
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 4,
                PlayoutSequence = 9,
            },
        }), ctx, CancellationToken.None);

        provider.InjectedEvents.ShouldHaveSingleItem();
        provider.InjectedEvents[0].EnvelopeId.ShouldBe("external-1");
        var persistedState = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        persistedState.PendingInjections.ShouldBeEmpty();
        persistedState.AwaitingInjectedResponseStart.ShouldBeTrue();
        persistedState.Status.ShouldBe(VoicePresenceRuntimeStatus.Idle);
        persistedState.LastDrainAckResponseId.ShouldBe(4);
    }

    [Fact]
    public async Task Attached_lease_drain_ack_should_forward_pending_event_injection_to_live_relay()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: true);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var state = roleAgent.State.VoicePresence[DefaultModuleName];
        state.Status = VoicePresenceRuntimeStatus.AudioDraining;
        state.CurrentResponseId = 4;
        state.NextResponseId = 5;
        state.PendingInjections.Add(new VoicePendingEventInjection
        {
            EnvelopeId = "external-lease",
            PublisherActorId = "external-agent",
            EventType = StringValue.Descriptor.FullName,
            Payload = Any.Pack(new StringValue { Value = "door opened" }),
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            DrainTimeoutExpired = new VoiceDrainTimeoutExpired
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ResponseId = 4,
            },
        }), ctx, CancellationToken.None);

        mediaPort.EventInjections.ShouldHaveSingleItem();
        mediaPort.EventInjections[0].TransportLeaseId.ShouldBe("transport-current");
        mediaPort.EventInjections[0].Injection.EnvelopeId.ShouldBe("external-lease");
        provider.InjectedEvents.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
        var persistedState = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        persistedState.PendingInjections.ShouldBeEmpty();
        persistedState.AwaitingInjectedResponseStart.ShouldBeTrue();
    }

    [Fact]
    public async Task Attached_lease_event_injection_should_not_reconnect_when_live_relay_missing()
    {
        var provider = new RecordingVoiceProvider();
        var mediaPort = new RecordingToolResultMediaPort(deliver: false);
        var services = new ServiceCollection()
            .AddSingleton<IVoiceVolatileMediaStreamPort>(mediaPort)
            .BuildServiceProvider();
        var module = CreateModule(provider);
        var roleAgent = CreateRoleAgentWithAttachedTransport();
        var state = roleAgent.State.VoicePresence[DefaultModuleName];
        state.Status = VoicePresenceRuntimeStatus.AudioDraining;
        state.CurrentResponseId = 4;
        state.NextResponseId = 5;
        state.PendingInjections.Add(new VoicePendingEventInjection
        {
            EnvelopeId = "external-miss",
            PublisherActorId = "external-agent",
            EventType = StringValue.Descriptor.FullName,
            Payload = Any.Pack(new StringValue { Value = "door opened" }),
            ObservedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
        var ctx = new StubEventHandlerContext(services, roleAgent);

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            DrainTimeoutExpired = new VoiceDrainTimeoutExpired
            {
                SessionId = "lease-current",
                OwnerId = "host-current",
                TransportLeaseId = "transport-current",
                LeaseEpoch = 7,
                ResponseId = 4,
            },
        }), ctx, CancellationToken.None);

        mediaPort.EventInjections.ShouldHaveSingleItem();
        provider.InjectedEvents.ShouldBeEmpty();
        provider.ConnectCalls.ShouldBe(0);
        var persistedState = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        persistedState.PendingInjections.ShouldBeEmpty();
        persistedState.AwaitingInjectedResponseStart.ShouldBeFalse();
    }

    [Fact]
    public async Task Immediate_external_injection_should_persist_and_clear_awaiting_fence_on_response_start()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateExternalPublication(new StringValue { Value = "alarm" }), ctx, CancellationToken.None);

        provider.InjectedEvents.ShouldHaveSingleItem();
        var injectedState = roleAgent.PersistedStates.ShouldHaveSingleItem().State;
        injectedState.AwaitingInjectedResponseStart.ShouldBeTrue();
        injectedState.PendingInjections.ShouldBeEmpty();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ProviderResponseId = "provider-r1" },
        }), ctx, CancellationToken.None);

        var responseStartedState = roleAgent.PersistedStates.Last().State;
        responseStartedState.AwaitingInjectedResponseStart.ShouldBeFalse();
        responseStartedState.Status.ShouldBe(VoicePresenceRuntimeStatus.ResponseInProgress);
        responseStartedState.ProviderResponseBindings.ShouldHaveSingleItem();
        responseStartedState.ProviderResponseBindings[0].ProviderResponseId.ShouldBe("provider-r1");
    }

    [Fact]
    public async Task Remote_session_open_and_provider_disconnect_should_persist_role_gagent_runtime_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var roleAgent = new RecordingRoleAgent("voice-agent");
        var ctx = new StubEventHandlerContext(agent: roleAgent);

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionOpenRequested = new VoiceRemoteSessionOpenRequested
            {
                SessionId = "remote-1",
            },
        }), ctx, CancellationToken.None);

        roleAgent.PersistedStates.ShouldHaveSingleItem().State.RemoteSessionId.ShouldBe("remote-1");
        roleAgent.State.VoicePresence["voice_presence"].ProviderResponseBindings.Add(new VoiceProviderResponseBinding
        {
            ProviderResponseId = "provider-r1",
            ResponseId = 3,
        });
        roleAgent.State.VoicePresence["voice_presence"].CancelledProviderResponseIds.Add("provider-r2");
        roleAgent.State.VoicePresence["voice_presence"].ActiveProviderResponseId = "provider-r1";

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Disconnected = new VoiceProviderDisconnected { Reason = "network" },
        }), ctx, CancellationToken.None);

        var disconnectedState = roleAgent.PersistedStates.Last().State;
        disconnectedState.RemoteSessionId.ShouldBeEmpty();
        disconnectedState.ProviderResponseBindings.ShouldBeEmpty();
        disconnectedState.CancelledProviderResponseIds.ShouldBeEmpty();
        disconnectedState.ActiveProviderResponseId.ShouldBeEmpty();
        var closed = ctx.PublishedEvents.ShouldHaveSingleItem().ShouldBeOfType<VoiceRemoteTransportOutput>();
        closed.SessionId.ShouldBe("remote-1");
        closed.SessionClosed.Reason.ShouldBe("provider_disconnected");
    }

    [Fact]
    public async Task Remote_session_signals_should_keep_lifecycle_but_not_forward_audio_chunks()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));
        await module.InitializeAsync(CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionOpenRequested = new VoiceRemoteSessionOpenRequested
            {
                SessionId = "remote-1",
            },
        }), ctx, CancellationToken.None);

        provider.AudioFrames.ShouldBeEmpty();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Disconnected = new VoiceProviderDisconnected
            {
                Reason = "network",
            },
        }), ctx, CancellationToken.None);

        ctx.PublishedEvents.Count.ShouldBe(1);
        var closedOutput = ctx.PublishedEvents[0].ShouldBeOfType<VoiceRemoteTransportOutput>();
        closedOutput.OutputCase.ShouldBe(VoiceRemoteTransportOutput.OutputOneofCase.SessionClosed);
        closedOutput.SessionClosed.Reason.ShouldBe("provider_disconnected");
    }

    [Fact]
    public async Task Null_payload_should_be_ignored()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        }, ctx, CancellationToken.None);

        ctx.Agent.ShouldBeOfType<RecordingRoleAgent>().State.VoicePresence.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisposeAsync_should_dispose_provider()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);

        await module.InitializeAsync(CancellationToken.None);
        module.IsInitialized.ShouldBeTrue();

        await module.DisposeAsync();
        module.IsInitialized.ShouldBeFalse();
        provider.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task Remote_session_open_should_publish_closed_when_module_not_initialized_or_transport_is_busy()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionOpenRequested = new VoiceRemoteSessionOpenRequested
            {
                SessionId = "remote-1",
            },
        }), ctx, CancellationToken.None);

        var notInitializedClose = ctx.PublishedEvents.ShouldHaveSingleItem().ShouldBeOfType<VoiceRemoteTransportOutput>();
        notInitializedClose.SessionClosed.Reason.ShouldBe("module_not_initialized");

        ctx.PublishedEvents.Clear();
        await module.InitializeAsync(CancellationToken.None);
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence["voice_presence"] = new VoicePresenceRuntimeState
        {
            ActiveSessionId = "lease-1",
            TransportAttached = true,
            ActiveTransportLeaseId = "transport-1",
            Initialized = true,
        };
        var busyCtx = new StubEventHandlerContext(agent: roleAgent);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionOpenRequested = new VoiceRemoteSessionOpenRequested
            {
                SessionId = "remote-2",
            },
        }), busyCtx, CancellationToken.None);

        var busyClose = busyCtx.PublishedEvents.ShouldHaveSingleItem().ShouldBeOfType<VoiceRemoteTransportOutput>();
        busyClose.SessionClosed.Reason.ShouldBe("transport_already_attached");
    }

    [Fact]
    public async Task Remote_session_inputs_and_close_should_ignore_audio_and_handle_control_and_close()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));
        await module.InitializeAsync(CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionOpenRequested = new VoiceRemoteSessionOpenRequested
            {
                SessionId = "remote-1",
            },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteControlInputReceived = new VoiceRemoteControlInputReceived
            {
                SessionId = "other",
                ControlFrame = new VoiceControlFrame
                {
                    DrainAcknowledged = new VoiceDrainAcknowledged { ResponseId = 1, PlayoutSequence = 2 },
                },
            },
        }), ctx, CancellationToken.None);

        provider.AudioFrames.ShouldBeEmpty();
        RoleVoiceState(ctx).LastDrainAckResponseId.ShouldBe(-1);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 8 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteControlInputReceived = new VoiceRemoteControlInputReceived
            {
                SessionId = "remote-1",
                ControlFrame = new VoiceControlFrame
                {
                    DrainAcknowledged = new VoiceDrainAcknowledged
                    {
                        ResponseId = 8,
                        PlayoutSequence = 9,
                    },
                },
            },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionCloseRequested = new VoiceRemoteSessionCloseRequested
            {
                SessionId = "other",
                Reason = "ignored",
            },
        }), ctx, CancellationToken.None);

        provider.AudioFrames.ShouldBeEmpty();
        RoleVoiceState(ctx).LastDrainAckResponseId.ShouldBe(8);
        ctx.PublishedEvents.ShouldBeEmpty();

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionCloseRequested = new VoiceRemoteSessionCloseRequested
            {
                SessionId = "remote-1",
                Reason = string.Empty,
            },
        }), ctx, CancellationToken.None);

        var closed = ctx.PublishedEvents.ShouldHaveSingleItem().ShouldBeOfType<VoiceRemoteTransportOutput>();
        closed.SessionClosed.Reason.ShouldBe("remote_session_closed");
    }

    [Fact]
    public async Task Function_call_should_return_error_when_tool_is_missing_or_throws()
    {
        var provider = new RecordingVoiceProvider();
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));
        var moduleWithoutInvoker = CreateModule(provider);

        await moduleWithoutInvoker.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "missing",
                ToolName = "doorbell.open",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        provider.ToolResults[0].ResultJson.ShouldContain("not available");

        provider.ToolResults.Clear();
        var throwingModule = CreateModule(provider, toolInvoker: new ThrowingVoiceToolInvoker("boom"));
        await throwingModule.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "broken",
                ToolName = "doorbell.open",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        provider.ToolResults[0].ResultJson.ShouldContain("execution failed: boom");
    }

    [Fact]
    public async Task Tool_catalog_failure_and_control_none_should_be_tolerated()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider, toolCatalog: new ThrowingVoiceToolCatalog());
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));

        await module.InitializeAsync(CancellationToken.None);
        await OpenLocalProviderSessionAsync(module, provider);
        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame()), ctx, CancellationToken.None);

        provider.LastSession.ShouldNotBeNull();
        provider.LastSession.ToolDefinitions.ShouldBeEmpty();
        ctx.Agent.ShouldBeOfType<RecordingRoleAgent>().State.VoicePresence.ShouldBeEmpty();
    }

    [Fact]
    public async Task External_event_injection_should_support_opaque_payload_fallback_and_zero_capacity_buffers()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(
            provider,
            options: new VoicePresenceModuleOptions
            {
                PendingInjectionCapacity = 0,
            });
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/custom.Unknown",
                Value = ByteString.CopyFrom([1, 2, 3]),
            },
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("external-agent", TopologyAudience.Children),
        }, ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        provider.InjectedEvents.ShouldHaveSingleItem();
        provider.InjectedEvents[0].PayloadJson.ShouldContain("valueBase64");

        var failureProvider = new RecordingVoiceProvider
        {
            ThrowOnInjectEvent = true,
        };
        var failureModule = CreateModule(failureProvider);
        var failureCtx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));
        await failureModule.InitializeAsync(CancellationToken.None);

        await failureModule.HandleAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = new Any
            {
                TypeUrl = "type.googleapis.com/google.protobuf.StringValue",
                Value = ByteString.CopyFrom([0x0A, 0xFF]),
            },
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("external-agent", TopologyAudience.Children),
        }, failureCtx, CancellationToken.None);

        failureProvider.InjectEventCalls.ShouldBe(1);
    }

    [Fact]
    public async Task InitializeAsync_should_replace_legacy_tools_with_sealed_catalog_definitions()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(
            provider,
            toolCatalog: new StaticVoiceToolCatalog(
            [
                new VoiceToolDefinition
                {
                    Name = "door.close",
                    Description = "close the front door",
                    ParametersSchema = """{"type":"object"}""",
                },
            ]));

        await module.InitializeAsync(CancellationToken.None);
        await OpenLocalProviderSessionAsync(module, provider);

        provider.LastSession.ShouldNotBeNull();
        provider.LastSession.ToolNames.ShouldBeEmpty();
        provider.LastSession.ToolDefinitions.Select(static x => x.Name)
            .ShouldBe(["door.close"]);
    }

    [Fact]
    public async Task Session_lease_should_merge_module_agent_and_route_voice_defaults()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var agent = new RecordingRoleAgent("voice-agent");
        agent.State.VoiceSessionDefaults[DefaultModuleName] = new VoiceSessionDefaults
        {
            Voice = "verse",
            Instructions = "agent default",
            SampleRateHz = 16000,
            TurnDetectionMode = VoiceTurnDetectionMode.ClientVad,
            VadDetectionThreshold = 0.35f,
            VadPrefixPaddingMs = 111,
            VadSilenceDurationMs = 222,
        };
        var ctx = new StubEventHandlerContext(agent: agent);

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = DefaultModuleName,
            SessionLeaseRequested = new VoicePresenceSessionLeaseRequested
            {
                SessionId = "lease-1",
                OwnerId = "voice-presence.host",
                ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(5)),
                SessionOverrides = new VoiceSessionOverrides
                {
                    Instructions = "route override",
                    SampleRateHz = 24000,
                    TurnDetectionMode = VoiceTurnDetectionMode.Disabled,
                },
            },
        }), ctx, CancellationToken.None);

        var active = RoleVoiceState(ctx).ActiveSessionConfig;
        active.ShouldNotBeNull();
        active.Voice.ShouldBe("verse");
        active.Instructions.ShouldBe("route override");
        active.SampleRateHz.ShouldBe(24000);
        active.TurnDetectionMode.ShouldBe(VoiceTurnDetectionMode.Disabled);
        active.VadDetectionThreshold.ShouldBe(0.35f);
        active.VadPrefixPaddingMs.ShouldBe(111);
        active.VadSilenceDurationMs.ShouldBe(222);
        RoleVoiceState(ctx).PcmSampleRateHz.ShouldBe(24000);
    }

    private static VoicePresenceModule CreateModule(
        RecordingVoiceProvider provider,
        string? linkId = null,
        IVoiceToolInvoker? toolInvoker = null,
        IVoiceToolCatalog? toolCatalog = null,
        VoicePresenceModuleOptions? options = null)
    {
        return new VoicePresenceModule(
            provider,
            new VoiceProviderConfig
            {
                ProviderName = "openai",
                Endpoint = "wss://example.test/realtime",
                ApiKey = "sk-test",
                Model = "gpt-realtime",
            },
            new VoiceSessionConfig
            {
                Voice = "alloy",
                Instructions = "be concise",
                SampleRateHz = 24000,
                ToolNames = { "doorbell.open" },
            },
            options ?? new VoicePresenceModuleOptions
            {
                LinkId = linkId,
            },
            toolInvoker,
            toolCatalog);
    }

    private static async Task OpenLocalProviderSessionAsync(
        VoicePresenceModule module,
        RecordingVoiceProvider provider)
    {
        var ctx = new StubEventHandlerContext(agent: new RecordingRoleAgent("voice-agent"));
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested
            {
                CallId = "tool-config",
                ToolName = "doorbell.open",
                ArgumentsJson = "{}",
                ResponseId = 1,
            },
        }), ctx, CancellationToken.None);

        provider.ConnectCalls.ShouldBeGreaterThan(0);
    }

    private static EventEnvelope CreateEnvelope(IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self),
        };
    }

    public static TheoryData<VoiceProviderEvent, VoiceRealtimeFrame.FrameOneofCase> RealtimeProjectionProviderEvents()
    {
        return new TheoryData<VoiceProviderEvent, VoiceRealtimeFrame.FrameOneofCase>
        {
            {
                new VoiceProviderEvent
                {
                    ResponseStarted = new VoiceResponseStarted
                    {
                        ProviderResponseId = "provider-response-1",
                        ResponseId = 1,
                    },
                },
                VoiceRealtimeFrame.FrameOneofCase.ResponseStarted
            },
            {
                new VoiceProviderEvent
                {
                    SpeechStarted = new VoiceSpeechStarted(),
                },
                VoiceRealtimeFrame.FrameOneofCase.SpeechStarted
            },
            {
                new VoiceProviderEvent
                {
                    ResponseDone = new VoiceResponseDone
                    {
                        ProviderResponseId = "provider-response-1",
                        ResponseId = 1,
                    },
                },
                VoiceRealtimeFrame.FrameOneofCase.ResponseDone
            },
            {
                new VoiceProviderEvent
                {
                    FunctionCall = new VoiceFunctionCallRequested
                    {
                        ProviderResponseId = "provider-response-1",
                        ResponseId = 1,
                        CallId = "call-1",
                        ToolName = "doorbell.open",
                    },
                },
                VoiceRealtimeFrame.FrameOneofCase.FunctionCall
            },
            {
                new VoiceProviderEvent
                {
                    Error = new VoiceProviderError
                    {
                        ErrorCode = "provider_error",
                        ErrorMessage = "provider failed",
                    },
                },
                VoiceRealtimeFrame.FrameOneofCase.Error
            },
            {
                new VoiceProviderEvent
                {
                    Disconnected = new VoiceProviderDisconnected
                    {
                        Reason = "provider_disconnected",
                    },
                },
                VoiceRealtimeFrame.FrameOneofCase.SessionClosed
            },
        };
    }

    private static RecordingRoleAgent CreateRoleAgentWithActiveSession()
    {
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence[DefaultModuleName] = new VoicePresenceRuntimeState
        {
            Initialized = true,
            RemoteSessionId = "session-1",
            ActiveSessionId = "session-1",
            LeaseEpoch = 7,
            Status = VoicePresenceRuntimeStatus.Idle,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        return roleAgent;
    }

    private static RecordingRoleAgent CreateRoleAgentWithAttachedTransport()
    {
        return CreateRoleAgentWithAttachedTransport(DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static RecordingRoleAgent CreateRoleAgentWithAttachedTransport(DateTimeOffset leaseExpiresAt)
    {
        var roleAgent = new RecordingRoleAgent("voice-agent");
        roleAgent.State.VoicePresence[DefaultModuleName] = new VoicePresenceRuntimeState
        {
            Initialized = true,
            ActiveSessionId = "lease-current",
            ActiveLeaseOwnerId = "host-current",
            LeaseExpiresAt = Timestamp.FromDateTimeOffset(leaseExpiresAt.ToUniversalTime()),
            TransportAttached = true,
            ActiveTransportLeaseId = "transport-current",
            LeaseEpoch = 7,
            Status = VoicePresenceRuntimeStatus.Idle,
            NextResponseId = 1,
            LastDrainAckResponseId = -1,
            LastDrainAckPlayoutSequence = -1,
        };
        return roleAgent;
    }

    private static void AddPendingClientToolCall(RecordingRoleAgent roleAgent, string callId)
    {
        roleAgent.State.VoicePresence[DefaultModuleName].PendingClientToolCalls.Add(new VoicePendingClientToolCall
        {
            CallId = callId,
            ToolName = "edge.light.toggle",
            SessionId = "lease-current",
            OwnerId = "host-current",
            TransportLeaseId = "transport-current",
            LeaseEpoch = 7,
            ExpiresAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(10)),
        });
    }

    private static EventEnvelope CreateExternalPublication(IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("external-agent", TopologyAudience.Children),
        };
    }

    private static VoicePresenceRuntimeState RoleVoiceState(
        StubEventHandlerContext ctx,
        string moduleName = DefaultModuleName)
    {
        var roleAgent = ctx.Agent.ShouldBeOfType<RecordingRoleAgent>();
        roleAgent.State.VoicePresence.TryGetValue(moduleName, out var state).ShouldBeTrue();
        return state;
    }

    private static bool IsSafeToInject(VoicePresenceRuntimeState state) =>
        state.Status == VoicePresenceRuntimeStatus.Idle &&
        (state.CurrentResponseId == 0 || state.LastDrainAckResponseId == state.CurrentResponseId);

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "aevatar.slnx")))
                return current.FullName;

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing aevatar.slnx.");
    }

    private static string StripLineComments(IEnumerable<string> lines) =>
        string.Join(Environment.NewLine, lines.Where(static line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private sealed class RecordingVoiceProvider : IRealtimeVoiceProvider
    {
        private Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task>? _eventSink;
        private VoiceProviderSessionKey _sessionKey = new(string.Empty, string.Empty, string.Empty, 0);

        public int ConnectCalls { get; private set; }

        public int UpdateSessionCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public bool Disposed { get; private set; }
        public VoiceSessionConfig? LastSession { get; private set; }
        public VoiceProviderSessionKey LastSessionKey => _sessionKey;
        public bool ThrowOnInjectEvent { get; set; }
        public int InjectEventCalls { get; private set; }

        public List<byte[]> AudioFrames { get; } = [];
        public List<VoiceInputImage> InputImages { get; } = [];
        public List<(string CallId, string ResultJson)> ToolResults { get; } = [];
        public List<VoiceConversationEventInjection> InjectedEvents { get; } = [];

        public Task<RealtimeVoiceProviderSession> ConnectAsync(
            VoiceProviderSessionKey sessionKey,
            VoiceProviderConfig config,
            Func<VoiceProviderSessionKey, VoiceProviderEvent, CancellationToken, Task> eventSink,
            Func<VoiceProviderSessionKey, VoiceProviderAudioFrame, CancellationToken, Task> audioSink,
            CancellationToken ct)
        {
            _ = config;
            _ = audioSink;
            _ = ct;
            ConnectCalls++;
            _sessionKey = sessionKey;
            _eventSink = eventSink;
            return Task.FromResult<RealtimeVoiceProviderSession>(new RecordingProviderSession(this));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task RaiseEventAsync(VoiceProviderEvent evt, CancellationToken ct) =>
            _eventSink?.Invoke(_sessionKey, evt, ct) ?? Task.CompletedTask;

        private sealed class RecordingProviderSession(RecordingVoiceProvider provider) : RealtimeVoiceProviderSession
        {
            public override Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
            {
                provider.AudioFrames.Add(pcm16.ToArray());
                return Task.CompletedTask;
            }

            public override Task SendInputImageAsync(VoiceInputImage inputImage, CancellationToken ct)
            {
                provider.InputImages.Add(inputImage.Clone());
                return Task.CompletedTask;
            }

            public override Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
            {
                provider.ToolResults.Add((callId, resultJson));
                return Task.CompletedTask;
            }

            public override Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
            {
                provider.InjectEventCalls++;
                if (provider.ThrowOnInjectEvent)
                    throw new InvalidOperationException("inject failed");

                provider.InjectedEvents.Add(injection.Clone());
                return Task.CompletedTask;
            }

            public override Task CancelResponseAsync(CancellationToken ct)
            {
                provider.CancelCalls++;
                return Task.CompletedTask;
            }

            public override Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
            {
                provider.UpdateSessionCalls++;
                provider.LastSession = session.Clone();
                return Task.CompletedTask;
            }

            public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingToolResultMediaPort(bool deliver) : IVoiceVolatileMediaStreamPort
    {
        public bool SupportsRemoteAudio => true;

        public List<string> CancelResponses { get; } = [];
        public List<(string TransportLeaseId, VoiceInputImage InputImage)> InputImages { get; } = [];
        public List<(string TransportLeaseId, string CallId, string ResultJson)> ToolResults { get; } = [];
        public List<(string TransportLeaseId, VoiceConversationEventInjection Injection)> EventInjections { get; } = [];

        public Task<bool> TryCancelResponseAsync(
            string transportLeaseId,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CancelResponses.Add(transportLeaseId);
            return Task.FromResult(deliver);
        }

        public Task<bool> TrySendInputImageAsync(
            string transportLeaseId,
            VoiceInputImage inputImage,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            InputImages.Add((transportLeaseId, inputImage.Clone()));
            return Task.FromResult(deliver);
        }

        public Task<bool> TrySendToolResultAsync(
            string transportLeaseId,
            string callId,
            string resultJson,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            ToolResults.Add((transportLeaseId, callId, resultJson));
            return Task.FromResult(deliver);
        }

        public Task<bool> TryInjectEventAsync(
            string transportLeaseId,
            VoiceConversationEventInjection injection,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            EventInjections.Add((transportLeaseId, injection.Clone()));
            return Task.FromResult(deliver);
        }

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            CancellationToken ct = default) =>
            AttachAsync(handle, transport, null, ct);

        public Task<VoiceTransportLifetimeCompleted?> AttachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport transport,
            VoiceToolCredentialTransportBinding? toolCredentialBinding,
            CancellationToken ct = default)
        {
            _ = toolCredentialBinding;
            return Task.FromResult<VoiceTransportLifetimeCompleted?>(null);
        }

        public Task DetachAsync(
            VoicePresenceSessionLeaseHandle handle,
            IVoiceTransport? expectedTransport,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CompleteTransportLifetimeAsync(
            VoicePresenceSessionLeaseHandle handle,
            VoiceTransportLifetimeCompleted? completed,
            string reason,
            CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingProjectionSessionEventHub : IProjectionSessionEventHub<VoiceRealtimeFrame>
    {
        public List<(string RootActorId, string SessionId, VoiceRealtimeFrame Frame)> Events { get; } = [];

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            VoiceRealtimeFrame evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Events.Add((rootActorId, sessionId, evt.Clone()));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<VoiceRealtimeFrame, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = sessionId;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new NoOpAsyncDisposable());
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StaticVoiceToolCatalog(IReadOnlyList<VoiceToolDefinition> tools) : IVoiceToolCatalog
    {
        public VoiceToolExecutionContext? LastToolContext { get; private set; }

        public Task<VoiceToolCatalogSnapshot> DiscoverAsync(
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            _ = ct;
            LastToolContext = toolContext?.Clone();
            return Task.FromResult(CreateToolCatalogSnapshot(tools));
        }
    }

    private sealed class StubEventHandlerContext(IServiceProvider? services = null, IAgent? agent = null) : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "voice-agent";

        public IServiceProvider Services { get; } = services ?? new ServiceCollection().BuildServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public IAgent Agent { get; } = agent ?? new StubAgent();

        public List<IMessage> PublishedEvents { get; } = [];

        public List<ScheduledSelfTimeout> ScheduledTimeouts { get; } = [];

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            PublishedEvents.Add(evt);
            _ = audience;
            _ = ct;
            _ = options;
            return Task.CompletedTask;
        }

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = targetActorId;
            _ = evt;
            _ = ct;
            _ = options;
            return Task.CompletedTask;
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimeoutAsync(
            string callbackId,
            TimeSpan dueTime,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default)
        {
            _ = options;
            ct.ThrowIfCancellationRequested();
            ScheduledTimeouts.Add(new ScheduledSelfTimeout(
                callbackId,
                dueTime,
                evt.Descriptor.Parser.ParseFrom(evt.ToByteArray())));
            return Task.FromResult(new RuntimeCallbackLease(AgentId, callbackId, ScheduledTimeouts.Count, RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleSelfDurableTimerAsync(
            string callbackId,
            TimeSpan dueTime,
            TimeSpan period,
            IMessage evt,
            EventEnvelopePublishOptions? options = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task CancelDurableCallbackAsync(RuntimeCallbackLease lease, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed record ScheduledSelfTimeout(string CallbackId, TimeSpan DueTime, IMessage Event);

    private sealed class StubAgent : IAgent
    {
        public string Id => "voice-agent";

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult("voice-agent");

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRoleAgent(string id) : IAgent, IVoicePresenceRuntimeStateOwner
    {
        public string Id => id;

        public RecordingRoleState State { get; } = new();

        public List<VoicePresenceRuntimeStateChangedEvent> PersistedStates { get; } = [];

        public bool TryGetVoicePresenceRuntimeState(string moduleName, out VoicePresenceRuntimeState runtimeState)
        {
            if (State.VoicePresence.TryGetValue(moduleName, out var stored))
            {
                runtimeState = stored.Clone();
                return true;
            }

            runtimeState = new VoicePresenceRuntimeState();
            return false;
        }

        public bool TryGetVoiceSessionDefaults(string moduleName, out VoiceSessionDefaults defaults)
        {
            if (State.VoiceSessionDefaults.TryGetValue(moduleName, out var stored))
            {
                defaults = stored.Clone();
                return true;
            }

            defaults = new VoiceSessionDefaults();
            return false;
        }

        public Task PersistVoicePresenceRuntimeStateAsync(
            string moduleName,
            VoicePresenceRuntimeState runtimeState,
            CancellationToken ct = default)
        {
            _ = ct;
            var evt = new VoicePresenceRuntimeStateChangedEvent
            {
                ModuleName = moduleName,
                State = runtimeState.Clone(),
            };
            PersistedStates.Add(evt);
            State.VoicePresence[moduleName] = runtimeState.Clone();
            return Task.CompletedTask;
        }

        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> GetDescriptionAsync() => Task.FromResult(id);

        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);

        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingRoleState
    {
        public Dictionary<string, VoicePresenceRuntimeState> VoicePresence { get; } = [];

        public Dictionary<string, VoiceSessionDefaults> VoiceSessionDefaults { get; } = [];
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }

    private sealed class RecordingVoiceToolInvoker(string resultJson) : IVoiceToolInvoker
    {
        public int Calls { get; private set; }
        public string? LastOwnerActorId { get; private set; }
        public string? LastSessionId { get; private set; }
        public string? LastCallId { get; private set; }
        public long LastIssuedAtUnixMs { get; private set; }
        public string? LastToolName { get; private set; }
        public string? LastArgumentsJson { get; private set; }
        public VoiceToolExecutionContext? LastToolContext { get; private set; }

        public Task<string> ExecuteAsync(
            string ownerActorId,
            string sessionId,
            string callId,
            long issuedAtUnixMs,
            string toolName,
            string argumentsJson,
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            _ = ct;
            Calls++;
            LastOwnerActorId = ownerActorId;
            LastSessionId = sessionId;
            LastCallId = callId;
            LastIssuedAtUnixMs = issuedAtUnixMs;
            LastToolName = toolName;
            LastArgumentsJson = argumentsJson;
            LastToolContext = toolContext?.Clone();
            return Task.FromResult(resultJson);
        }
    }

    private sealed class BlockingVoiceToolInvoker : IVoiceToolInvoker
    {
        public async Task<string> ExecuteAsync(
            string ownerActorId,
            string sessionId,
            string callId,
            long issuedAtUnixMs,
            string toolName,
            string argumentsJson,
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            _ = ownerActorId;
            _ = sessionId;
            _ = callId;
            _ = issuedAtUnixMs;
            _ = toolName;
            _ = argumentsJson;
            _ = toolContext;

            var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        }
    }

    private sealed class ThrowingVoiceToolInvoker(string message) : IVoiceToolInvoker
    {
        public Task<string> ExecuteAsync(
            string ownerActorId,
            string sessionId,
            string callId,
            long issuedAtUnixMs,
            string toolName,
            string argumentsJson,
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            _ = ownerActorId;
            _ = sessionId;
            _ = callId;
            _ = issuedAtUnixMs;
            _ = toolName;
            _ = argumentsJson;
            _ = toolContext;
            _ = ct;
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ThrowingVoiceToolCatalog : IVoiceToolCatalog
    {
        public Task<VoiceToolCatalogSnapshot> DiscoverAsync(
            VoiceToolExecutionContext? toolContext = null,
            CancellationToken ct = default)
        {
            _ = toolContext;
            _ = ct;
            throw new InvalidOperationException("catalog failed");
        }
    }

    private static VoiceToolCatalogSnapshot CreateToolCatalogSnapshot(
        IReadOnlyList<VoiceToolDefinition> definitions)
    {
        foreach (var definition in definitions)
        {
            if (definition.Owner == VoiceToolOwner.Unspecified)
                definition.Owner = VoiceToolOwner.Actor;
        }

        var snapshot = new VoiceToolCatalogSnapshot
        {
            PolicyVersion = "test-voice-policy/v1",
            Proof = new VoiceAgentTurnToolCatalogProof
            {
                ToolCount = definitions.Count,
                SchemaBytes = definitions.Sum(static definition =>
                    System.Text.Encoding.UTF8.GetByteCount(definition.ParametersSchema ?? string.Empty)),
                CatalogDigest = "sha256:test",
                MaximumToolCount = VoiceToolCatalogSnapshotValidator.MaximumToolCount,
                MaximumSchemaBytes = VoiceToolCatalogSnapshotValidator.MaximumSchemaBytes,
            },
        };
        snapshot.Tools.AddRange(definitions.Select(static definition => definition.Clone()));
        snapshot.Proof.ToolDescriptors.AddRange(definitions.Select(static definition =>
            new VoiceAgentTurnToolDescriptorProof
            {
                Name = definition.Name,
                ExactDescription = definition.Description,
                CanonicalSchema = Google.Protobuf.ByteString.CopyFromUtf8(definition.ParametersSchema ?? string.Empty),
                SchemaSha256 = "sha256:test",
                OriginKind = "Voice",
            }));
        return snapshot;
    }

}
