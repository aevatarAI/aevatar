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

public class VoicePresenceEventInjectionTests
{
    [Fact]
    public async Task External_publication_when_safe_should_inject_immediately()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider, timeProvider: timeProvider);
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(
            CreateExternalEnvelope("doorbell-1", "ding", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        provider.InjectedEvents.ShouldHaveSingleItem();
        provider.InjectedEvents[0].EnvelopeId.ShouldNotBeNullOrWhiteSpace();
        provider.InjectedEvents[0].PublisherActorId.ShouldBe("doorbell-1");
        provider.InjectedEvents[0].EventType.ShouldBe("type.googleapis.com/google.protobuf.StringValue");
        provider.InjectedEvents[0].PayloadJson.ShouldBe("\"ding\"");
    }

    [Fact]
    public async Task External_publication_during_response_should_buffer_until_cancel_reopens_fence()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider, timeProvider: timeProvider);
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(
            CreateExternalEnvelope("sensor-1", "temp-high", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        provider.InjectedEvents.ShouldBeEmpty();

        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        provider.InjectedEvents.ShouldHaveSingleItem();
        provider.InjectedEvents[0].PublisherActorId.ShouldBe("sensor-1");
    }

    [Fact]
    public async Task Buffered_event_should_be_dropped_when_it_becomes_stale_before_flush()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(
            provider,
            timeProvider: timeProvider,
            staleAfter: TimeSpan.FromSeconds(5));
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(
            CreateExternalEnvelope("sensor-1", "temp-high", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(6));

        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        provider.InjectedEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Duplicate_buffered_events_within_window_should_only_inject_once()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider, timeProvider: timeProvider);
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(
            CreateExternalEnvelope("sensor-1", "temp-high", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(1));

        await module.HandleAsync(
            CreateExternalEnvelope("sensor-1", "temp-high", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        provider.InjectedEvents.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Pending_buffer_should_drop_oldest_event_when_capacity_is_reached()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(
            provider,
            timeProvider: timeProvider,
            pendingInjectionCapacity: 1);
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseStarted = new VoiceResponseStarted { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        await module.HandleAsync(
            CreateExternalEnvelope("sensor-1", "first", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromMilliseconds(10));

        await module.HandleAsync(
            CreateExternalEnvelope("sensor-1", "second", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        await module.HandleAsync(CreateVoiceEnvelope(new VoiceProviderEvent
        {
            ResponseCancelled = new VoiceResponseCancelled { ResponseId = 1 },
        }), ctx, CancellationToken.None);

        provider.InjectedEvents.ShouldHaveSingleItem();
        provider.InjectedEvents[0].PayloadJson.ShouldBe("\"second\"");
    }

    [Fact]
    public async Task Self_published_publication_should_not_be_injected()
    {
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 14, 9, 0, 0, TimeSpan.Zero));
        var provider = new RecordingVoiceProvider();
        var module = CreateModule(provider, timeProvider: timeProvider);
        var ctx = new StubEventHandlerContext();

        await module.InitializeAsync(CancellationToken.None);
        await module.HandleAsync(
            CreateExternalEnvelope(ctx.AgentId, "internal", timeProvider.GetUtcNow()),
            ctx,
            CancellationToken.None);

        provider.InjectedEvents.ShouldBeEmpty();
    }

    private static VoicePresenceModule CreateModule(
        RecordingVoiceProvider provider,
        TimeProvider? timeProvider = null,
        TimeSpan? staleAfter = null,
        int pendingInjectionCapacity = 16) =>
        new(
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
            },
            new VoicePresenceModuleOptions
            {
                StaleAfter = staleAfter ?? TimeSpan.FromSeconds(10),
                DedupeWindow = TimeSpan.FromSeconds(2),
                PendingInjectionCapacity = pendingInjectionCapacity,
                TimeProvider = timeProvider ?? TimeProvider.System,
            });

    private static EventEnvelope CreateExternalEnvelope(
        string publisherActorId,
        string value,
        DateTimeOffset observedAt) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(observedAt),
            Payload = Any.Pack(new StringValue { Value = value }),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication(publisherActorId, TopologyAudience.Children),
        };

    private static EventEnvelope CreateVoiceEnvelope(IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateTopologyPublication("voice-agent", TopologyAudience.Self),
        };

    private sealed class RecordingVoiceProvider : IRealtimeVoiceProvider
    {
        public List<VoiceConversationEventInjection> InjectedEvents { get; } = [];

        public Func<VoiceProviderEvent, CancellationToken, Task>? OnEvent { private get; set; }

        public Task ConnectAsync(VoiceProviderConfig config, CancellationToken ct)
        {
            _ = config;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendAudioAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            _ = pcm16;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task SendToolResultAsync(string callId, string resultJson, CancellationToken ct)
        {
            _ = callId;
            _ = resultJson;
            _ = ct;
            return Task.CompletedTask;
        }

        public Task InjectEventAsync(VoiceConversationEventInjection injection, CancellationToken ct)
        {
            _ = ct;
            InjectedEvents.Add(injection.Clone());
            return Task.CompletedTask;
        }

        public Task CancelResponseAsync(CancellationToken ct)
        {
            _ = ct;
            return Task.CompletedTask;
        }

        public Task UpdateSessionAsync(VoiceSessionConfig session, CancellationToken ct)
        {
            _ = session;
            _ = ct;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubEventHandlerContext : IEventHandlerContext
    {
        public EventEnvelope InboundEnvelope { get; } = new();

        public string AgentId => "voice-agent";

        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        public Microsoft.Extensions.Logging.ILogger Logger { get; } = NullLogger.Instance;

        public IAgent Agent { get; } = new StubAgent();

        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage
        {
            _ = evt;
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

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
