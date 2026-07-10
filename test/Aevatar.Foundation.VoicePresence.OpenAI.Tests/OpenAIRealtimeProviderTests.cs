using System.Runtime.CompilerServices;
using System.Text.Json;
using Aevatar.Foundation.VoicePresence.Abstractions;
using Aevatar.Foundation.VoicePresence.OpenAI.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using OpenAI.Realtime;
using Shouldly;

namespace Aevatar.Foundation.VoicePresence.OpenAI.Tests;

public class OpenAIRealtimeProviderTests
{
    [Fact]
    public async Task UpdateSession_should_configure_voice_vad_and_permissive_tools()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            Instructions = "be concise",
            SampleRateHz = 24000,
            ToolNames = { "door.open" },
        }, CancellationToken.None);

        session.SessionUpdateEvents.Count.ShouldBe(2);
        using var document = JsonDocument.Parse(session.SessionUpdateEvents.Last());
        var root = document.RootElement;
        root.GetProperty("type").GetString().ShouldBe("session.update");
        root.TryGetProperty("input_audio_format", out _).ShouldBeFalse();
        root.TryGetProperty("output_audio_format", out _).ShouldBeFalse();
        root.TryGetProperty("modalities", out _).ShouldBeFalse();

        var configuredSession = root.GetProperty("session");
        configuredSession.GetProperty("type").GetString().ShouldBe("realtime");
        configuredSession.GetProperty("instructions").GetString().ShouldBe("be concise");
        configuredSession.GetProperty("output_modalities").EnumerateArray()
            .Select(static x => x.GetString()).ShouldBe(["audio"]);
        configuredSession.TryGetProperty("input_audio_format", out _).ShouldBeFalse();
        configuredSession.TryGetProperty("output_audio_format", out _).ShouldBeFalse();
        configuredSession.TryGetProperty("modalities", out _).ShouldBeFalse();

        var audio = configuredSession.GetProperty("audio");
        var input = audio.GetProperty("input");
        input.GetProperty("format").GetProperty("type").GetString().ShouldBe("audio/pcm");
        input.GetProperty("format").GetProperty("rate").GetInt32().ShouldBe(24000);
        var turnDetection = input.GetProperty("turn_detection");
        turnDetection.GetProperty("type").GetString().ShouldBe("server_vad");
        turnDetection.GetProperty("threshold").GetSingle().ShouldBe(0.7f);
        turnDetection.GetProperty("prefix_padding_ms").GetInt32().ShouldBe(300);
        turnDetection.GetProperty("silence_duration_ms").GetInt32().ShouldBe(600);
        turnDetection.GetProperty("interrupt_response").GetBoolean().ShouldBeTrue();
        turnDetection.GetProperty("create_response").GetBoolean().ShouldBeTrue();

        var output = audio.GetProperty("output");
        output.GetProperty("format").GetProperty("type").GetString().ShouldBe("audio/pcm");
        output.GetProperty("format").GetProperty("rate").GetInt32().ShouldBe(24000);
        output.GetProperty("voice").GetString().ShouldBe("alloy");

        var tools = configuredSession.GetProperty("tools").EnumerateArray().ToArray();
        tools.Length.ShouldBe(1);
        tools[0].GetProperty("type").GetString().ShouldBe("function");
        tools[0].GetProperty("name").GetString().ShouldBe("door.open");
        tools[0].GetProperty("parameters").GetProperty("additionalProperties").GetBoolean().ShouldBeTrue();
        configuredSession.GetProperty("tool_choice").GetString().ShouldBe("auto");
    }

    [Fact]
    public async Task UpdateSession_should_register_structured_tool_definitions_before_legacy_names()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            SampleRateHz = 24000,
            ToolDefinitions =
            {
                new VoiceToolDefinition
                {
                    Name = "door.open",
                    Description = "open the front door",
                    ParametersSchema = """{"type":"object","properties":{"door":{"type":"string"}}}""",
                },
            },
            ToolNames = { "door.open", "door.close" },
        }, CancellationToken.None);

        using var document = JsonDocument.Parse(session.SessionUpdateEvents.Last());
        var tools = document.RootElement.GetProperty("session").GetProperty("tools").EnumerateArray().ToArray();
        tools.Length.ShouldBe(2);

        var openTool = tools[0];
        openTool.GetProperty("name").GetString().ShouldBe("door.open");
        openTool.GetProperty("description").GetString().ShouldBe("open the front door");
        openTool.GetProperty("parameters").GetProperty("properties").GetProperty("door")
            .GetProperty("type").GetString().ShouldBe("string");

        var closeTool = tools[1];
        closeTool.GetProperty("name").GetString().ShouldBe("door.close");
        closeTool.GetProperty("description").GetString().ShouldBe("Aevatar tool 'door.close'.");
    }

    [Fact]
    public async Task UpdateSession_should_use_session_turn_detection_policy()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);
        var providerSession = await ConnectAsync(provider);

        await providerSession.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            SampleRateHz = 24000,
            TurnDetectionMode = VoiceTurnDetectionMode.ServerVad,
            VadDetectionThreshold = 0.45f,
            VadPrefixPaddingMs = 125,
            VadSilenceDurationMs = 375,
        }, CancellationToken.None);
        await providerSession.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            SampleRateHz = 24000,
            TurnDetectionMode = VoiceTurnDetectionMode.Disabled,
        }, CancellationToken.None);

        using var serverVadDocument = JsonDocument.Parse(session.SessionUpdateEvents[1]);
        var turnDetection = serverVadDocument.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("turn_detection");
        turnDetection.GetProperty("type").GetString().ShouldBe("server_vad");
        turnDetection.GetProperty("threshold").GetSingle().ShouldBe(0.45f);
        turnDetection.GetProperty("prefix_padding_ms").GetInt32().ShouldBe(125);
        turnDetection.GetProperty("silence_duration_ms").GetInt32().ShouldBe(375);

        using var disabledDocument = JsonDocument.Parse(session.SessionUpdateEvents[2]);
        disabledDocument.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("turn_detection")
            .ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task SendToolResult_should_add_function_output_and_request_followup_response()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.SendToolResultAsync("call-1", """{"ok":true}""", CancellationToken.None);

        session.AddedItems.Count.ShouldBe(1);
        session.AddedItems[0].ShouldBeOfType<RealtimeFunctionCallOutputItem>();
        var output = (RealtimeFunctionCallOutputItem)session.AddedItems[0];
        output.CallId.ShouldBe("call-1");
        output.FunctionOutput.ShouldBe("""{"ok":true}""");
        session.StartResponseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task InjectEvent_should_add_user_message_and_start_response()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.InjectEventAsync(new VoiceConversationEventInjection
        {
            EnvelopeId = "evt-1",
            EventType = "type.googleapis.com/google.protobuf.StringValue",
            PayloadJson = """{"value":"phase two"}""",
        }, CancellationToken.None);

        session.AddedItems.Count.ShouldBe(1);
        session.AddedItems[0].ShouldBeOfType<RealtimeMessageItem>();
        var message = (RealtimeMessageItem)session.AddedItems[0];
        message.Role.ToString().ShouldBe("user");
        message.Content.Count.ShouldBe(1);
        message.Content[0].ShouldBeOfType<RealtimeInputTextMessageContentPart>();
        ((RealtimeInputTextMessageContentPart)message.Content[0]).Text.ShouldContain("phase two");
        session.StartResponseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task InjectEvent_should_add_structured_user_message_and_start_response()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.InjectEventAsync(new VoiceConversationEventInjection
        {
            EnvelopeId = "evt-1",
            PublisherActorId = "doorbell-1",
            EventType = "type.googleapis.com/google.protobuf.StringValue",
            PayloadJson = """{"@type":"type.googleapis.com/google.protobuf.StringValue","value":"ding"}""",
        }, CancellationToken.None);

        session.AddedItems.Count.ShouldBe(1);
        session.AddedItems[0].ShouldBeOfType<RealtimeMessageItem>();
        var message = (RealtimeMessageItem)session.AddedItems[0];
        message.Role.ToString().ShouldBe("user");
        message.Content.Count.ShouldBe(1);
        message.Content[0].ShouldBeOfType<RealtimeInputTextMessageContentPart>();
        var text = ((RealtimeInputTextMessageContentPart)message.Content[0]).Text;
        text.ShouldContain("External event observed");
        text.ShouldContain("\"envelopeId\": \"evt-1\"");
        text.ShouldContain("\"publisherActorId\": \"doorbell-1\"");
        session.StartResponseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Receive_loop_should_map_openai_events_to_voice_provider_events()
    {
        var session = new FakeSession(
        [
            new OpenAIRealtimeSpeechStartedEvent(),
            new OpenAIRealtimeResponseCreatedEvent("resp-1"),
            new OpenAIRealtimeOutputAudioDeltaEvent("resp-1", [1, 2, 3]),
            new OpenAIRealtimeFunctionCallEvent("resp-1", "call-1", "door.open", """{"room":"kitchen"}"""),
            new OpenAIRealtimeResponseFinishedEvent("resp-1", Cancelled: false),
            new OpenAIRealtimeResponseCreatedEvent("resp-2"),
            new OpenAIRealtimeResponseFinishedEvent("resp-2", Cancelled: true),
            new OpenAIRealtimeErrorEvent("rate_limit", "slow down"),
            new OpenAIRealtimeDisconnectedEvent("socket-closed"),
        ]);
        var provider = CreateProvider(session);
        var events = new List<VoiceProviderEvent>();
        var audioFrames = new List<VoiceProviderAudioFrame>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await ConnectAsync(provider, events, evt =>
        {
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                disconnected.TrySetResult();
        }, audioFrames);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        events.Select(x => x.EventCase).ShouldBe(
        [
            VoiceProviderEvent.EventOneofCase.SpeechStarted,
            VoiceProviderEvent.EventOneofCase.ResponseStarted,
            VoiceProviderEvent.EventOneofCase.FunctionCall,
            VoiceProviderEvent.EventOneofCase.ResponseDone,
            VoiceProviderEvent.EventOneofCase.ResponseStarted,
            VoiceProviderEvent.EventOneofCase.ResponseCancelled,
            VoiceProviderEvent.EventOneofCase.Error,
            VoiceProviderEvent.EventOneofCase.Disconnected,
        ]);
        events[1].ResponseStarted.ResponseId.ShouldBe(0);
        events[1].ResponseStarted.ProviderResponseId.ShouldBe("resp-1");
        audioFrames.ShouldHaveSingleItem().SampleRateHz.ShouldBe(24000);
        audioFrames.Single().ProviderResponseId.ShouldBe("resp-1");
        events[2].FunctionCall.ResponseId.ShouldBe(0);
        events[2].FunctionCall.ProviderResponseId.ShouldBe("resp-1");
        events[3].ResponseDone.ResponseId.ShouldBe(0);
        events[3].ResponseDone.ProviderResponseId.ShouldBe("resp-1");
        events[4].ResponseStarted.ResponseId.ShouldBe(0);
        events[4].ResponseStarted.ProviderResponseId.ShouldBe("resp-2");
        events[5].ResponseCancelled.ResponseId.ShouldBe(0);
        events[5].ResponseCancelled.ProviderResponseId.ShouldBe("resp-2");
        events[6].Error.ErrorCode.ShouldBe("rate_limit");
    }

    [Fact]
    public async Task Receive_loop_should_suppress_benign_realtime_race_errors()
    {
        var session = new FakeSession(
        [
            new OpenAIRealtimeErrorEvent("response_cancel_not_active", "Cancellation failed: no active response found"),
            new OpenAIRealtimeErrorEvent("conversation_already_has_active_response", "Conversation already has an active response"),
            new OpenAIRealtimeErrorEvent("rate_limit", "slow down"),
            new OpenAIRealtimeDisconnectedEvent("done"),
        ]);
        var provider = CreateProvider(session);
        var events = new List<VoiceProviderEvent>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await ConnectAsync(provider, events, evt =>
        {
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                disconnected.TrySetResult();
        });
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Benign idempotent realtime races — an explicit response.cancel that loses to OpenAI's
        // server-side interrupt_response (response_cancel_not_active), or a redundant response.create
        // (conversation_already_has_active_response) — must NOT reach the client as a fatal provider
        // error. A genuine error (rate_limit) still surfaces.
        var errors = events.Where(x => x.EventCase == VoiceProviderEvent.EventOneofCase.Error).ToList();
        errors.Count.ShouldBe(1);
        errors[0].Error.ErrorCode.ShouldBe("rate_limit");
    }

    [Fact]
    public async Task Receive_loop_should_emit_events_directly_through_physical_session_sink()
    {
        var session = new FakeSession(
        [
            new OpenAIRealtimeResponseCreatedEvent("resp-1"),
            new OpenAIRealtimeOutputAudioDeltaEvent("resp-1", [1]),
            new OpenAIRealtimeOutputAudioDeltaEvent("resp-1", [2]),
            new OpenAIRealtimeOutputAudioDeltaEvent("resp-1", [3]),
            new OpenAIRealtimeDisconnectedEvent("done"),
        ]);
        var provider = CreateProvider(session);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<VoiceProviderEvent>();
        var audioFrames = new List<VoiceProviderAudioFrame>();

        await provider.ConnectAsync(new VoiceProviderSessionKey("lease-1", "host-1", "transport-1", 1), CreateConfig(),
            (key, evt, ct) =>
            {
                _ = key;
                _ = ct;
                seen.Add(evt);
                if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                    disconnected.TrySetResult();
                return Task.CompletedTask;
            },
            (key, audio, ct) =>
            {
                _ = key;
                _ = ct;
                audioFrames.Add(audio);
                return Task.CompletedTask;
            },
            CancellationToken.None);
        await session.ReceiveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        audioFrames.Count.ShouldBe(3);
        audioFrames.Select(static x => x.Pcm16.ToArray().Single())
            .ShouldBe([1, 2, 3]);
        seen.Any(static x => x.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected).ShouldBeTrue();
    }

    [Fact]
    public async Task SendAudio_should_forward_pcm16_to_session()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.SendAudioAsync(new byte[] { 10, 20, 30 }, CancellationToken.None);

        session.SentAudio.Count.ShouldBe(1);
        session.SentAudio[0].ToArray().ShouldBe([10, 20, 30]);
    }

    [Fact]
    public async Task SendAudio_empty_should_be_noop()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.SendAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        session.SentAudio.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendInputImage_should_emit_openai_conversation_item_and_start_response()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.SendInputImageAsync(new VoiceInputImage
        {
            MediaType = "image/png",
            Data = Google.Protobuf.ByteString.CopyFrom([1, 2, 3]),
        }, CancellationToken.None);

        session.InputImageEvents.Count.ShouldBe(1);
        using var document = JsonDocument.Parse(session.InputImageEvents.Single());
        var root = document.RootElement;
        root.GetProperty("type").GetString().ShouldBe("conversation.item.create");
        var item = root.GetProperty("item");
        item.GetProperty("type").GetString().ShouldBe("message");
        item.GetProperty("role").GetString().ShouldBe("user");
        var content = item.GetProperty("content")[0];
        content.GetProperty("type").GetString().ShouldBe("input_image");
        content.GetProperty("image_url").GetString()
            .ShouldBe($"data:image/png;base64,{Convert.ToBase64String([1, 2, 3])}");
        session.StartResponseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task SendInputImage_empty_should_be_noop()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);
        await providerSession.SendInputImageAsync(new VoiceInputImage
        {
            MediaType = "image/png",
        }, CancellationToken.None);

        session.InputImageEvents.ShouldBeEmpty();
        session.StartResponseCalls.ShouldBe(0);
    }

    [Fact]
    public async Task SendInputImage_should_reject_non_image_media_type()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);
        var providerSession = await ConnectAsync(provider);

        await Should.ThrowAsync<ArgumentException>(() =>
            providerSession.SendInputImageAsync(new VoiceInputImage
            {
                MediaType = "application/octet-stream",
                Data = Google.Protobuf.ByteString.CopyFrom([1]),
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Unsupported_sample_rate_should_be_rejected()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var providerSession = await ConnectAsync(provider);

        await Should.ThrowAsync<InvalidOperationException>(() => providerSession.UpdateSessionAsync(new VoiceSessionConfig
        {
            SampleRateHz = 16000,
        }, CancellationToken.None));
    }

    [Fact]
    public async Task Connect_with_wrong_provider_name_should_throw()
    {
        var provider = CreateProvider(new FakeSession());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ConnectAsync(provider, config: new VoiceProviderConfig
            {
                ProviderName = "azure",
                ApiKey = "sk-test",
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Connect_without_api_key_should_throw()
    {
        var provider = CreateProvider(new FakeSession());

        await Should.ThrowAsync<InvalidOperationException>(() =>
            ConnectAsync(provider, config: new VoiceProviderConfig
            {
                ProviderName = "openai",
            }, CancellationToken.None));
    }

    [Fact]
    public async Task Connector_should_allow_independent_physical_sessions()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        var first = await ConnectAsync(provider);
        var second = await ConnectAsync(provider);

        second.ShouldNotBeSameAs(first);
    }

    [Fact]
    public async Task Connect_should_configure_no_auto_response_before_readiness_update()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        await using var providerSession = await ConnectAsync(provider);

        session.SessionUpdateEvents.Count.ShouldBe(1);
        using var document = JsonDocument.Parse(session.SessionUpdateEvents.Single());
        var turnDetection = document.RootElement
            .GetProperty("session")
            .GetProperty("audio")
            .GetProperty("input")
            .GetProperty("turn_detection");
        turnDetection.GetProperty("type").GetString().ShouldBe("server_vad");
        turnDetection.GetProperty("create_response").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task SendToolResult_with_empty_callId_should_throw()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);
        var providerSession = await ConnectAsync(provider);

        await Should.ThrowAsync<ArgumentException>(() =>
            providerSession.SendToolResultAsync("", "{}", CancellationToken.None));
    }

    [Fact]
    public async Task InjectEvent_with_null_event_should_throw()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);
        var providerSession = await ConnectAsync(provider);

        await Should.ThrowAsync<ArgumentNullException>(() =>
            providerSession.InjectEventAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_before_connect_should_not_throw()
    {
        var provider = CreateProvider(new FakeSession());
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Double_dispose_should_not_throw()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);
        await ConnectAsync(provider);

        await provider.DisposeAsync();
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Connect_after_dispose_should_throw()
    {
        var provider = CreateProvider(new FakeSession());
        await provider.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(() =>
            ConnectAsync(provider));
    }

    [Fact]
    public async Task UpdateSession_with_zero_sample_rate_should_use_default()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);
        var providerSession = await ConnectAsync(provider);
        await providerSession.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            SampleRateHz = 0,
        }, CancellationToken.None);

        session.SessionUpdateEvents.Count.ShouldBe(2);
    }

    [Fact]
    public async Task UpdateSession_without_tools_should_not_set_tool_choice()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);
        var providerSession = await ConnectAsync(provider);
        await providerSession.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            Instructions = "test",
        }, CancellationToken.None);

        using var document = JsonDocument.Parse(session.SessionUpdateEvents.Last());
        var configuredSession = document.RootElement.GetProperty("session");
        configuredSession.GetProperty("tools").GetArrayLength().ShouldBe(0);
        configuredSession.TryGetProperty("tool_choice", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task Receive_loop_error_should_produce_disconnected_event()
    {
        var session = new ErrorSession();
        var provider = CreateProvider(session);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await ConnectAsync(provider, [], evt =>
        {
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                disconnected.TrySetResult();
        });
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Callback_exception_should_not_crash_dispatch_loop()
    {
        var session = new FakeSession(
        [
            new OpenAIRealtimeSpeechStartedEvent(),
            new OpenAIRealtimeSpeechStoppedEvent(),
            new OpenAIRealtimeDisconnectedEvent("done"),
        ]);
        var provider = CreateProvider(session);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;

        await ConnectAsync(provider, [], evt =>
        {
            Interlocked.Increment(ref callCount);
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.SpeechStarted)
                throw new InvalidOperationException("callback boom");
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                disconnected.TrySetResult();
        });
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        callCount.ShouldBeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task Connect_uses_resolver_provided_ephemeral_key_over_static_config()
    {
        var session = new FakeSession();
        var factory = new FakeSessionFactory(session);
        var provider = new OpenAIRealtimeProvider(
            factory,
            new OpenAIRealtimeProviderOptions(),
            NullLogger<OpenAIRealtimeProvider>.Instance,
            new StubCredentialResolver("ek_session_123"));

        await using var providerSession = await provider.ConnectAsync(
            new VoiceProviderSessionKey("session-1", "owner-1", "transport-1", 1),
            new VoiceProviderConfig { ProviderName = "openai", ApiKey = string.Empty, Model = "gpt-realtime" },
            static (_, _, _) => Task.CompletedTask,
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        factory.LastConfig.ShouldNotBeNull();
        factory.LastConfig!.ApiKey.ShouldBe("ek_session_123");
    }

    [Fact]
    public async Task Connect_without_resolver_uses_static_config_key()
    {
        var session = new FakeSession();
        var factory = new FakeSessionFactory(session);
        var provider = new OpenAIRealtimeProvider(
            factory,
            new OpenAIRealtimeProviderOptions(),
            NullLogger<OpenAIRealtimeProvider>.Instance);

        await using var providerSession = await provider.ConnectAsync(
            new VoiceProviderSessionKey("session-1", "owner-1", "transport-1", 1),
            CreateConfig(),
            static (_, _, _) => Task.CompletedTask,
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        factory.LastConfig!.ApiKey.ShouldBe("sk-test");
    }

    [Fact]
    public async Task Connect_when_resolver_returns_null_falls_back_to_static_config_key()
    {
        var session = new FakeSession();
        var factory = new FakeSessionFactory(session);
        var provider = new OpenAIRealtimeProvider(
            factory,
            new OpenAIRealtimeProviderOptions(),
            NullLogger<OpenAIRealtimeProvider>.Instance,
            new StubCredentialResolver(null));

        await using var providerSession = await provider.ConnectAsync(
            new VoiceProviderSessionKey("session-1", "owner-1", "transport-1", 1),
            CreateConfig(),
            static (_, _, _) => Task.CompletedTask,
            static (_, _, _) => Task.CompletedTask,
            CancellationToken.None);

        factory.LastConfig!.ApiKey.ShouldBe("sk-test");
    }

    private static OpenAIRealtimeProvider CreateProvider(
        IOpenAIRealtimeSession session,
        OpenAIRealtimeProviderOptions? options = null)
    {
        return new OpenAIRealtimeProvider(
            new FakeSessionFactory(session),
            options ?? new OpenAIRealtimeProviderOptions(),
            NullLogger<OpenAIRealtimeProvider>.Instance);
    }

    private static VoiceProviderConfig CreateConfig() =>
        new()
        {
            ProviderName = "openai",
            ApiKey = "sk-test",
            Model = "gpt-realtime",
        };

    private static Task<RealtimeVoiceProviderSession> ConnectAsync(
        OpenAIRealtimeProvider provider,
        VoiceProviderConfig? config = null,
        CancellationToken ct = default) =>
        ConnectAsync(provider, [], onEvent: null, config: config, ct: ct);

    private static Task<RealtimeVoiceProviderSession> ConnectAsync(
        OpenAIRealtimeProvider provider,
        List<VoiceProviderEvent> events,
        Action<VoiceProviderEvent>? onEvent = null,
        List<VoiceProviderAudioFrame>? audioFrames = null,
        VoiceProviderConfig? config = null,
        CancellationToken ct = default) =>
        provider.ConnectAsync(
            new VoiceProviderSessionKey("lease-1", "host-1", "transport-1", 1),
            config ?? CreateConfig(),
            (key, evt, token) =>
            {
                _ = key;
                _ = token;
                events.Add(evt);
                onEvent?.Invoke(evt);
                return Task.CompletedTask;
            },
            (key, audioFrame, token) =>
            {
                _ = key;
                _ = token;
                audioFrames?.Add(audioFrame);
                return Task.CompletedTask;
            },
            ct);

    private sealed class StubCredentialResolver(string? apiKey) : IRealtimeProviderCredentialResolver
    {
        public Task<string?> ResolveApiKeyAsync(
            VoiceProviderSessionKey sessionKey,
            VoiceProviderConfig config,
            CancellationToken ct) =>
            Task.FromResult(apiKey);
    }

    private sealed class FakeSessionFactory : IOpenAIRealtimeSessionFactory
    {
        private readonly IOpenAIRealtimeSession _session;

        public FakeSessionFactory(IOpenAIRealtimeSession session)
        {
            _session = session;
        }

        public VoiceProviderConfig? LastConfig { get; private set; }

        public Task<IOpenAIRealtimeSession> StartConversationSessionAsync(
            VoiceProviderConfig config,
            string defaultModel,
            CancellationToken ct)
        {
            LastConfig = config;
            _ = defaultModel;
            _ = ct;
            return Task.FromResult(_session);
        }
    }

    private sealed class FakeSession : IOpenAIRealtimeSession
    {
        private readonly IReadOnlyList<OpenAIRealtimeSessionEvent> _events;
        private readonly Task? _afterFirstEvent;

        public FakeSession(
            IReadOnlyList<OpenAIRealtimeSessionEvent>? events = null,
            Task? afterFirstEvent = null)
        {
            _events = events ?? [];
            _afterFirstEvent = afterFirstEvent;
        }

        public List<string> SessionUpdateEvents { get; } = [];

        public List<BinaryData> SentAudio { get; } = [];

        public List<string> InputImageEvents { get; } = [];

        public List<RealtimeItem> AddedItems { get; } = [];

        public int StartResponseCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public TaskCompletionSource ReceiveCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SendSessionUpdateAsync(BinaryData sessionUpdateEvent, CancellationToken ct)
        {
            _ = ct;
            SessionUpdateEvents.Add(sessionUpdateEvent.ToString());
            return Task.CompletedTask;
        }

        public Task SendInputAudioAsync(BinaryData audio, CancellationToken ct)
        {
            _ = ct;
            SentAudio.Add(audio);
            return Task.CompletedTask;
        }

        public Task SendInputImageAsync(BinaryData inputImageEvent, CancellationToken ct)
        {
            _ = ct;
            InputImageEvents.Add(inputImageEvent.ToString());
            return Task.CompletedTask;
        }

        public Task AddItemAsync(RealtimeItem item, CancellationToken ct)
        {
            _ = ct;
            AddedItems.Add(item);
            return Task.CompletedTask;
        }

        public Task StartResponseAsync(CancellationToken ct)
        {
            _ = ct;
            StartResponseCalls++;
            return Task.CompletedTask;
        }

        public Task CancelResponseAsync(CancellationToken ct)
        {
            _ = ct;
            CancelCalls++;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<OpenAIRealtimeSessionEvent> ReceiveEventsAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < _events.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return _events[i];
                if (i == 0 && _afterFirstEvent is not null)
                    await _afterFirstEvent.WaitAsync(ct);

                await Task.Yield();
            }

            ReceiveCompleted.TrySetResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ErrorSession : IOpenAIRealtimeSession
    {
        public Task SendSessionUpdateAsync(BinaryData sessionUpdateEvent, CancellationToken ct) => Task.CompletedTask;
        public Task SendInputAudioAsync(BinaryData audio, CancellationToken ct) => Task.CompletedTask;
        public Task SendInputImageAsync(BinaryData inputImageEvent, CancellationToken ct) => Task.CompletedTask;
        public Task AddItemAsync(RealtimeItem item, CancellationToken ct) => Task.CompletedTask;
        public Task StartResponseAsync(CancellationToken ct) => Task.CompletedTask;
        public Task CancelResponseAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public async IAsyncEnumerable<OpenAIRealtimeSessionEvent> ReceiveEventsAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (ct.IsCancellationRequested)
                yield break;

            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("simulated connection error");
        }
    }
}
