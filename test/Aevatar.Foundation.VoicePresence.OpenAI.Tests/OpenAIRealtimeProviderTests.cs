using System.Runtime.CompilerServices;
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

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await provider.UpdateSessionAsync(new VoiceSessionConfig
        {
            Voice = "alloy",
            Instructions = "be concise",
            SampleRateHz = 24000,
            ToolNames = { "door.open" },
        }, CancellationToken.None);

        session.ConfiguredOptions.Count.ShouldBe(1);
        var options = session.ConfiguredOptions.Single();
        options.Instructions.ShouldBe("be concise");
        options.OutputModalities.Select(x => x.ToString()).ShouldBe(["audio"]);
        options.AudioOptions.OutputAudioOptions.Voice?.ToString().ShouldBe("alloy");
        options.AudioOptions.InputAudioOptions.TurnDetection.ShouldNotBeNull();
        options.Tools.Count.ShouldBe(1);
        options.Tools[0].ShouldBeOfType<RealtimeFunctionTool>();
        ((RealtimeFunctionTool)options.Tools[0]).FunctionName.ShouldBe("door.open");
    }

    [Fact]
    public async Task SendToolResult_should_add_function_output_and_request_followup_response()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await provider.SendToolResultAsync("call-1", """{"ok":true}""", CancellationToken.None);

        session.AddedItems.Count.ShouldBe(1);
        session.AddedItems[0].ShouldBeOfType<RealtimeFunctionCallOutputItem>();
        var output = (RealtimeFunctionCallOutputItem)session.AddedItems[0];
        output.CallId.ShouldBe("call-1");
        output.FunctionOutput.ShouldBe("""{"ok":true}""");
        session.StartResponseCalls.ShouldBe(1);
    }

    [Fact]
    public async Task InjectUserText_should_add_user_message_and_start_response()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await provider.InjectUserTextAsync("phase two", CancellationToken.None);

        session.AddedItems.Count.ShouldBe(1);
        session.AddedItems[0].ShouldBeOfType<RealtimeMessageItem>();
        var message = (RealtimeMessageItem)session.AddedItems[0];
        message.Role.ToString().ShouldBe("user");
        message.Content.Count.ShouldBe(1);
        message.Content[0].ShouldBeOfType<RealtimeInputTextMessageContentPart>();
        ((RealtimeInputTextMessageContentPart)message.Content[0]).Text.ShouldBe("phase two");
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
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        provider.OnEvent = (evt, ct) =>
        {
            _ = ct;
            events.Add(evt);
            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        events.Select(x => x.EventCase).ShouldBe(
        [
            VoiceProviderEvent.EventOneofCase.SpeechStarted,
            VoiceProviderEvent.EventOneofCase.ResponseStarted,
            VoiceProviderEvent.EventOneofCase.AudioReceived,
            VoiceProviderEvent.EventOneofCase.FunctionCall,
            VoiceProviderEvent.EventOneofCase.ResponseDone,
            VoiceProviderEvent.EventOneofCase.ResponseStarted,
            VoiceProviderEvent.EventOneofCase.ResponseCancelled,
            VoiceProviderEvent.EventOneofCase.Error,
            VoiceProviderEvent.EventOneofCase.Disconnected,
        ]);
        events[1].ResponseStarted.ResponseId.ShouldBe(1);
        events[2].AudioReceived.SampleRateHz.ShouldBe(24000);
        events[3].FunctionCall.ResponseId.ShouldBe(1);
        events[4].ResponseDone.ResponseId.ShouldBe(1);
        events[5].ResponseStarted.ResponseId.ShouldBe(2);
        events[6].ResponseCancelled.ResponseId.ShouldBe(2);
        events[7].Error.ErrorCode.ShouldBe("rate_limit");
    }

    [Fact]
    public async Task Slow_callback_should_keep_latest_events_when_channel_is_bounded()
    {
        var session = new FakeSession(
        [
            new OpenAIRealtimeResponseCreatedEvent("resp-1"),
            new OpenAIRealtimeOutputAudioDeltaEvent("resp-1", [1]),
            new OpenAIRealtimeOutputAudioDeltaEvent("resp-1", [2]),
            new OpenAIRealtimeOutputAudioDeltaEvent("resp-1", [3]),
            new OpenAIRealtimeDisconnectedEvent("done"),
        ]);
        var provider = CreateProvider(session, new OpenAIRealtimeProviderOptions
        {
            EventQueueCapacity = 2,
        });
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<VoiceProviderEvent>();
        var invocationCount = 0;

        provider.OnEvent = async (evt, ct) =>
        {
            seen.Add(evt);
            if (Interlocked.Increment(ref invocationCount) == 1)
                await gate.Task.WaitAsync(ct);

            if (evt.EventCase == VoiceProviderEvent.EventOneofCase.Disconnected)
                disconnected.TrySetResult();
        };

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await session.ReceiveCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.TrySetResult();
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        seen.Select(x => x.EventCase).ShouldBe(
        [
            VoiceProviderEvent.EventOneofCase.ResponseStarted,
            VoiceProviderEvent.EventOneofCase.AudioReceived,
            VoiceProviderEvent.EventOneofCase.Disconnected,
        ]);
        seen[1].AudioReceived.Pcm16.ToByteArray().ShouldBe([3]);
    }

    [Fact]
    public async Task SendAudio_should_forward_pcm16_to_session()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await provider.SendAudioAsync(new byte[] { 10, 20, 30 }, CancellationToken.None);

        session.SentAudio.Count.ShouldBe(1);
        session.SentAudio[0].ToArray().ShouldBe([10, 20, 30]);
    }

    [Fact]
    public async Task SendAudio_empty_should_be_noop()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);
        await provider.SendAudioAsync(ReadOnlyMemory<byte>.Empty, CancellationToken.None);

        session.SentAudio.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unsupported_sample_rate_should_be_rejected()
    {
        var session = new FakeSession();
        var provider = CreateProvider(session);

        await provider.ConnectAsync(CreateConfig(), CancellationToken.None);

        await Should.ThrowAsync<InvalidOperationException>(() => provider.UpdateSessionAsync(new VoiceSessionConfig
        {
            SampleRateHz = 16000,
        }, CancellationToken.None));
    }

    private static OpenAIRealtimeProvider CreateProvider(
        FakeSession session,
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

    private sealed class FakeSessionFactory : IOpenAIRealtimeSessionFactory
    {
        private readonly FakeSession _session;

        public FakeSessionFactory(FakeSession session)
        {
            _session = session;
        }

        public Task<IOpenAIRealtimeSession> StartConversationSessionAsync(
            VoiceProviderConfig config,
            string defaultModel,
            CancellationToken ct)
        {
            _ = config;
            _ = defaultModel;
            _ = ct;
            return Task.FromResult<IOpenAIRealtimeSession>(_session);
        }
    }

    private sealed class FakeSession : IOpenAIRealtimeSession
    {
        private readonly IReadOnlyList<OpenAIRealtimeSessionEvent> _events;

        public FakeSession(IReadOnlyList<OpenAIRealtimeSessionEvent>? events = null)
        {
            _events = events ?? [];
        }

        public List<RealtimeConversationSessionOptions> ConfiguredOptions { get; } = [];

        public List<BinaryData> SentAudio { get; } = [];

        public List<RealtimeItem> AddedItems { get; } = [];

        public int StartResponseCalls { get; private set; }

        public int CancelCalls { get; private set; }

        public TaskCompletionSource ReceiveCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConfigureConversationSessionAsync(RealtimeConversationSessionOptions options, CancellationToken ct)
        {
            _ = ct;
            ConfiguredOptions.Add(options);
            return Task.CompletedTask;
        }

        public Task SendInputAudioAsync(BinaryData audio, CancellationToken ct)
        {
            _ = ct;
            SentAudio.Add(audio);
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
            foreach (var evt in _events)
            {
                ct.ThrowIfCancellationRequested();
                yield return evt;
                await Task.Yield();
            }

            ReceiveCompleted.TrySetResult();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
