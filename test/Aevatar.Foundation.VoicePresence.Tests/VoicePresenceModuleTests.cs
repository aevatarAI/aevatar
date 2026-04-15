using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.VoicePresence;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.Modules;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.Tests;

public class VoicePresenceModuleTests
{
    [Fact]
    public async Task Initialize_and_audio_fast_path_should_forward_provider_calls()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider, linkId: "user-audio");

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAudioAsync(
            new VoiceAudioFastPathFrame("user-audio", new byte[] { 1, 2, 3 }, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await module.DisposeAsync();

        provider.ConnectCalls.ShouldBe(1);
        provider.UpdateSessionCalls.ShouldBe(1);
        provider.AudioFrames.Single().ShouldBe(new byte[] { 1, 2, 3 });
        provider.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task InitializeAsync_should_be_idempotent_and_expose_priority()
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

        provider.ConnectCalls.ShouldBe(1);
        provider.UpdateSessionCalls.ShouldBe(1);
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
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }), ctx, CancellationToken.None);

        provider.CancelCalls.ShouldBe(1);
        module.StateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);
        module.StateMachine.CurrentResponseId.ShouldBe(1);
    }

    [Fact]
    public async Task Response_done_and_drain_ack_should_release_injection_fence()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

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

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
        module.StateMachine.IsSafeToInject.ShouldBeTrue();
    }

    [Fact]
    public async Task Response_done_should_transition_to_audio_draining()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseDone = new VoiceResponseDone { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.AudioDraining);
    }

    [Fact]
    public async Task Response_cancelled_should_return_to_idle()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public async Task Speech_stopped_should_not_change_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStarted = new VoiceSpeechStarted(),
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            SpeechStopped = new VoiceSpeechStopped(),
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.UserSpeaking);
    }

    [Fact]
    public async Task Provider_disconnected_should_reset_to_idle()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Disconnected = new VoiceProviderDisconnected { Reason = "test" },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public async Task Noop_provider_events_should_not_change_state()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            AudioReceived = new VoiceAudioReceived { Pcm16 = Google.Protobuf.ByteString.CopyFrom([1, 2]), SampleRateHz = 24000 },
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            FunctionCall = new VoiceFunctionCallRequested { CallId = "c1", ToolName = "t", ArgumentsJson = "{}", ResponseId = 1 },
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Error = new VoiceProviderError { ErrorCode = "e", ErrorMessage = "msg" },
        }), ctx, CancellationToken.None);
        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public async Task Function_call_should_execute_tool_and_send_result()
    {
        var provider = new RecordingVoiceProvider();
        var invoker = new RecordingVoiceToolInvoker("""{"ok":true}""");
        var module = CreateModule(provider, toolInvoker: invoker);
        var ctx = new StubEventHandlerContext();

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
        invoker.LastToolName.ShouldBe("doorbell.open");
        invoker.LastArgumentsJson.ShouldBe("""{"force":true}""");
        provider.ToolResults.ShouldHaveSingleItem();
        provider.ToolResults[0].CallId.ShouldBe("call-1");
        provider.ToolResults[0].ResultJson.ShouldBe("""{"ok":true}""");
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
        var ctx = new StubEventHandlerContext();

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
    public async Task Module_signal_should_ignore_events_for_other_voice_module_aliases()
    {
        var module = CreateModule(
            new RecordingVoiceProvider(),
            options: new VoicePresenceModuleOptions
            {
                Name = "voice_presence_openai",
            });
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence_minicpm",
            ProviderEvent = new VoiceProviderEvent
            {
                ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
            },
        }), ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public async Task Remote_session_signals_should_forward_audio_and_publish_remote_outputs()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext();
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
            RemoteAudioInputReceived = new VoiceRemoteAudioInputReceived
            {
                SessionId = "remote-1",
                Pcm16 = ByteString.CopyFrom([5, 6]),
            },
        }), ctx, CancellationToken.None);

        provider.AudioFrames.ShouldHaveSingleItem();
        provider.AudioFrames[0].ShouldBe([5, 6]);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            AudioReceived = new VoiceAudioReceived
            {
                Pcm16 = ByteString.CopyFrom([7, 8]),
                SampleRateHz = 24000,
            },
        }), ctx, CancellationToken.None);

        ctx.PublishedEvents.ShouldHaveSingleItem();
        var audioOutput = ctx.PublishedEvents[0];
        audioOutput.ShouldBeOfType<VoiceRemoteTransportOutput>();
        ((VoiceRemoteTransportOutput)audioOutput).SessionId.ShouldBe("remote-1");
        ((VoiceRemoteTransportOutput)audioOutput).OutputCase.ShouldBe(VoiceRemoteTransportOutput.OutputOneofCase.AudioOutput);

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            Disconnected = new VoiceProviderDisconnected
            {
                Reason = "network",
            },
        }), ctx, CancellationToken.None);

        ctx.PublishedEvents.Count.ShouldBe(2);
        var closedOutput = ctx.PublishedEvents[1].ShouldBeOfType<VoiceRemoteTransportOutput>();
        closedOutput.OutputCase.ShouldBe(VoiceRemoteTransportOutput.OutputOneofCase.SessionClosed);
        closedOutput.SessionClosed.Reason.ShouldBe("provider_disconnected");
    }

    [Fact]
    public async Task Null_payload_should_be_ignored()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var ctx = new StubEventHandlerContext();

        await module.HandleAsync(new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
        }, ctx, CancellationToken.None);

        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
    }

    [Fact]
    public void HandleAudio_wrong_link_should_throw()
    {
        var module = CreateModule(new RecordingVoiceProvider(), linkId: "link-a");

        Should.Throw<InvalidOperationException>(() =>
            module.HandleAudioAsync(
                new VoiceAudioFastPathFrame("link-b", new byte[] { 1 }, DateTimeOffset.UtcNow),
                CancellationToken.None));
    }

    [Fact]
    public void CanHandleAudio_empty_linkId_matches_any()
    {
        var module = CreateModule(new RecordingVoiceProvider(), linkId: null);

        module.CanHandleAudio(new VoiceAudioFastPathFrame("any-link", new byte[] { 1 }, DateTimeOffset.UtcNow))
            .ShouldBeTrue();
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
    public async Task DisposeAsync_should_detach_and_dispose_attached_transport()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var transport = new PassiveVoiceTransport();

        await module.InitializeAsync(CancellationToken.None);
        module.AttachTransport(transport, static (_, _) => Task.CompletedTask);

        await module.DisposeAsync();

        transport.Disposed.ShouldBeTrue();
        module.IsTransportAttached.ShouldBeFalse();
        provider.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task AttachTransport_should_reject_when_transport_or_remote_session_is_already_attached()
    {
        var module = CreateModule(new RecordingVoiceProvider());
        var firstTransport = new PassiveVoiceTransport();

        module.AttachTransport(firstTransport, static (_, _) => Task.CompletedTask);
        Should.Throw<InvalidOperationException>(() =>
            module.AttachTransport(new PassiveVoiceTransport(), static (_, _) => Task.CompletedTask));

        await module.DetachTransportAsync(firstTransport);

        var ctx = new StubEventHandlerContext();
        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionOpenRequested = new VoiceRemoteSessionOpenRequested
            {
                SessionId = "remote-1",
            },
        }), ctx, CancellationToken.None);

        Should.Throw<InvalidOperationException>(() =>
            module.AttachTransport(new PassiveVoiceTransport(), static (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task Relay_and_provider_audio_send_failures_should_be_swallowed()
    {
        var provider = new RecordingVoiceProvider();
        var receiveThrowTransport = new ThrowingReceiveVoiceTransport();
        var module = CreateModule(provider);

        module.AttachTransport(receiveThrowTransport, static (_, _) => Task.CompletedTask);
        await receiveThrowTransport.ReceiveAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await module.DetachTransportAsync(receiveThrowTransport);

        var sendThrowTransport = new ThrowingSendVoiceTransport();
        module.AttachTransport(sendThrowTransport, static (_, _) => Task.CompletedTask);
        await provider.RaiseEventAsync(new VoiceProviderEvent
        {
            AudioReceived = new VoiceAudioReceived
            {
                Pcm16 = ByteString.CopyFrom([7, 8]),
                SampleRateHz = 24000,
            },
        }, CancellationToken.None);

        sendThrowTransport.SendAttempts.ShouldBe(1);
        await module.DetachTransportAsync(sendThrowTransport);
    }

    [Fact]
    public async Task EnsureSelfEventDispatcher_should_use_context_dispatch_port_and_tolerate_dispatch_failures()
    {
        var dispatchPort = new RecordingDispatchPort();
        var services = new ServiceCollection()
            .AddSingleton<IActorDispatchPort>(dispatchPort)
            .BuildServiceProvider();
        var ctx = new StubEventHandlerContext(services);
        var module = CreateModule(new RecordingVoiceProvider());
        var dispatchMethod = typeof(VoicePresenceModule).GetMethod(
            "DispatchSelfEventAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await module.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);
        await ((Task)dispatchMethod.Invoke(module, new object[] { new VoiceControlFrame
        {
            DrainAcknowledged = new VoiceDrainAcknowledged
            {
                ResponseId = 1,
                PlayoutSequence = 9,
            },
        }, CancellationToken.None })!);

        dispatchPort.Dispatches.ShouldHaveSingleItem();

        var throwingServices = new ServiceCollection()
            .AddSingleton<IActorDispatchPort>(new ThrowingDispatchPort())
            .BuildServiceProvider();
        var throwingCtx = new StubEventHandlerContext(throwingServices);
        var throwingModule = CreateModule(new RecordingVoiceProvider());
        await throwingModule.HandleAsync(CreateEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 2 },
        }), throwingCtx, CancellationToken.None);
        await ((Task)dispatchMethod.Invoke(throwingModule, new object[] { new VoiceControlFrame(), CancellationToken.None })!);
    }

    [Fact]
    public async Task Remote_session_open_should_publish_closed_when_module_not_initialized_or_transport_is_busy()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext();

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
        module.AttachTransport(new PassiveVoiceTransport(), static (_, _) => Task.CompletedTask);

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteSessionOpenRequested = new VoiceRemoteSessionOpenRequested
            {
                SessionId = "remote-2",
            },
        }), ctx, CancellationToken.None);

        var busyClose = ctx.PublishedEvents.ShouldHaveSingleItem().ShouldBeOfType<VoiceRemoteTransportOutput>();
        busyClose.SessionClosed.Reason.ShouldBe("transport_already_attached");
    }

    [Fact]
    public async Task Remote_session_inputs_and_close_should_ignore_mismatches_and_handle_matches()
    {
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider);
        var ctx = new StubEventHandlerContext();
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
            RemoteAudioInputReceived = new VoiceRemoteAudioInputReceived
            {
                SessionId = "other",
                Pcm16 = ByteString.CopyFrom([1, 2]),
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

        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteAudioInputReceived = new VoiceRemoteAudioInputReceived
            {
                SessionId = "remote-1",
                Pcm16 = ByteString.CopyFrom([3, 4]),
            },
        }), ctx, CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceModuleSignal
        {
            ModuleName = "voice_presence",
            RemoteControlInputReceived = new VoiceRemoteControlInputReceived
            {
                SessionId = "remote-1",
                ControlFrame = new VoiceControlFrame(),
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

        provider.AudioFrames.ShouldHaveSingleItem();
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
        var ctx = new StubEventHandlerContext();
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
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateEnvelope(new VoiceControlFrame()), ctx, CancellationToken.None);

        provider.LastSession.ShouldNotBeNull();
        provider.LastSession.ToolDefinitions.ShouldBeEmpty();
        module.StateMachine.State.ShouldBe(VoicePresenceState.Idle);
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

        provider.InjectedEvents.ShouldBeEmpty();

        var failureProvider = new RecordingVoiceProvider
        {
            ThrowOnInjectEvent = true,
        };
        var failureModule = CreateModule(failureProvider);
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
        }, ctx, CancellationToken.None);

        failureProvider.InjectEventCalls.ShouldBe(1);
    }

    [Fact]
    public async Task InitializeAsync_should_merge_discovered_tool_definitions_into_session()
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

        provider.LastSession.ShouldNotBeNull();
        provider.LastSession.ToolNames.ShouldContain("doorbell.open");
        provider.LastSession.ToolDefinitions.Select(static x => x.Name).ShouldContain("door.close");
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

    private sealed class RecordingVoiceProvider : IRealtimeVoiceProvider
    {
        public int ConnectCalls { get; private set; }

        public int UpdateSessionCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public bool Disposed { get; private set; }
        public VoiceSessionConfig? LastSession { get; private set; }
        public bool ThrowOnInjectEvent { get; set; }
        public int InjectEventCalls { get; private set; }

        public List<byte[]> AudioFrames { get; } = [];
        public List<(string CallId, string ResultJson)> ToolResults { get; } = [];
        public List<VoiceConversationEventInjection> InjectedEvents { get; } = [];

        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct)
        {
            _ = config;
            _ = ct;
            ConnectCalls++;
            return Task.CompletedTask;
        }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = ct;
            AudioFrames.Add(pcm16.ToArray());
            return Task.CompletedTask;
        }

        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            _ = ct;
            ToolResults.Add((callId, resultJson));
            return Task.CompletedTask;
        }

        public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
        {
            _ = ct;
            InjectEventCalls++;
            if (ThrowOnInjectEvent)
                throw new InvalidOperationException("inject failed");

            InjectedEvents.Add(injection.Clone());
            return Task.CompletedTask;
        }

        public Task CancelResponseAsync(CancellationToken ct)
        {
            _ = ct;
            CancelCalls++;
            return Task.CompletedTask;
        }

        public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
        {
            _ = ct;
            UpdateSessionCalls++;
            LastSession = session.Clone();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }

        public Task RaiseEventAsync(VoiceProviderEvent evt, CancellationToken ct) =>
            OnEvent?.Invoke(evt, ct) ?? Task.CompletedTask;
    }

    private sealed class StaticVoiceToolCatalog(IReadOnlyList<VoiceToolDefinition> tools) : IVoiceToolCatalog
    {
        public Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAsync(CancellationToken ct = default)
        {
            _ = ct;
            return Task.FromResult(tools);
        }
    }

    private sealed class StubEventHandlerContext(IServiceProvider? services = null) : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "voice-agent";

        public IServiceProvider Services { get; } = services ?? new ServiceCollection().BuildServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public IAgent Agent { get; } = new StubAgent();

        public List<IMessage> PublishedEvents { get; } = [];

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
            CancellationToken ct = default) =>
            throw new NotSupportedException();

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

    private sealed class RecordingVoiceToolInvoker(string resultJson) : IVoiceToolInvoker
    {
        public int Calls { get; private set; }
        public string? LastToolName { get; private set; }
        public string? LastArgumentsJson { get; private set; }

        public Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        {
            _ = ct;
            Calls++;
            LastToolName = toolName;
            LastArgumentsJson = argumentsJson;
            return Task.FromResult(resultJson);
        }
    }

    private sealed class BlockingVoiceToolInvoker : IVoiceToolInvoker
    {
        public async Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        {
            _ = toolName;
            _ = argumentsJson;

            var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = ct.Register(() => gate.TrySetCanceled(ct));
            return await gate.Task;
        }
    }

    private sealed class ThrowingVoiceToolInvoker(string message) : IVoiceToolInvoker
    {
        public Task<string> ExecuteAsync(string toolName, string argumentsJson, CancellationToken ct = default)
        {
            _ = toolName;
            _ = argumentsJson;
            _ = ct;
            throw new InvalidOperationException(message);
        }
    }

    private sealed class ThrowingVoiceToolCatalog : IVoiceToolCatalog
    {
        public Task<IReadOnlyList<VoiceToolDefinition>> DiscoverAsync(CancellationToken ct = default)
        {
            _ = ct;
            throw new InvalidOperationException("catalog failed");
        }
    }

    private sealed class PassiveVoiceTransport : IVoiceTransport
    {
        public bool Disposed { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            _ = frame;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingReceiveVoiceTransport : IVoiceTransport
    {
        public TaskCompletionSource ReceiveAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            _ = frame;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            ReceiveAttempted.TrySetResult();
            await Task.Yield();
            throw new InvalidOperationException("receive failed");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingSendVoiceTransport : IVoiceTransport
    {
        public int SendAttempts { get; private set; }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            SendAttempts++;
            throw new InvalidOperationException("send failed");
        }

        public Task SendControlAsync(VoiceControlFrame frame, CancellationToken ct)
        {
            _ = frame;
            _ = ct;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<VoiceTransportFrame> ReceiveFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            _ = ct;
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Dispatches { get; } = [];

        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = ct;
            Dispatches.Add((actorId, envelope.Clone()));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingDispatchPort : IActorDispatchPort
    {
        public Task DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default)
        {
            _ = actorId;
            _ = envelope;
            _ = ct;
            throw new InvalidOperationException("dispatch failed");
        }
    }
}
