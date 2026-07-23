using System.Reflection;
using System.Text;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class NyxRelayTextOperationTimeoutPayloadTests
{
    [Fact]
    public async Task HandleLlmReplyStreamChunkAsync_ScheduledTimeoutPayload_StripsRuntimeRelayCredentials()
    {
        await using var callbackHarness = await RuntimeCallbackSchedulerGrainTestHarness.StartAsync();
        var agent = await CreateAgentAsync("conv-nyx-timeout-sanitize", callbackHarness.Scheduler);

        await agent.HandleLlmReplyStreamChunkAsync(CreateStreamChunk());

        var scheduled = callbackHarness.Timeouts.Should().ContainSingle().Subject;
        var timeout = scheduled.TriggerEnvelope.Payload.Unpack<NyxRelayTextOperationTimeoutFiredEvent>();
        timeout.Chunk.Should().NotBeNull();
        timeout.Chunk.ReplyToken.Should().BeEmpty();
        timeout.Chunk.ReplyTokenExpiresAtUnixMs.Should().Be(0);
        timeout.Chunk.Activity.TransportExtras.NyxUserAccessToken.Should().BeEmpty();
        timeout.Chunk.CorrelationId.Should().Be("corr-timeout-token");
        timeout.Chunk.RegistrationId.Should().Be("reg-1");
        timeout.Chunk.AccumulatedText.Should().Be("hello");
        timeout.Chunk.Activity.OutboundDelivery.ReplyMessageId.Should().Be("relay-msg-1");

        var persistedBytes = scheduled.TriggerEnvelope.ToByteArray();
        Encoding.UTF8.GetString(persistedBytes).Should().NotContain("runtime-reply-token-secret");
        Encoding.UTF8.GetString(persistedBytes).Should().NotContain("runtime-user-access-token-secret");
    }

    [Fact]
    public async Task HandleNyxRelayTextOperationTimeoutFiredAsync_FinalCompletion_PersistsDeliveredRunIdFromPendingRequest()
    {
        await using var callbackHarness = await RuntimeCallbackSchedulerGrainTestHarness.StartAsync();
        var store = new InMemoryEventStore();
        var agent = await CreateAgentAsync("conv-nyx-final-timeout-run-id", callbackHarness.Scheduler, store);
        var chunk = CreateStreamChunk();

        agent.State.PendingLlmReplyRequests.Add(new NeedsLlmReplyEvent
        {
            CorrelationId = chunk.CorrelationId,
            RunId = "run-stream-final-timeout",
            TargetActorId = agent.Id,
            RegistrationId = chunk.RegistrationId,
            Activity = chunk.Activity.Clone(),
            RequestedAtUnixMs = 10,
        });
        agent.State.ActiveReplyLifecycles.Add(new ConversationReplyLifecycleState
        {
            CorrelationId = chunk.CorrelationId,
            Mode = ConversationReplyLifecycleMode.NyxRelayText,
            Phase = ConversationReplyLifecyclePhase.TextStreaming,
            PlatformMessageId = "relay-msg-1",
            LastFlushedText = "partial text",
            EditCount = 1,
            NyxRelayInFlightOperation = NyxRelayTextOperationKind.Final,
            NyxRelayInFlightSequence = 2,
            NyxRelayOperationGeneration = 3,
        });

        await agent.HandleNyxRelayTextOperationTimeoutFiredAsync(new NyxRelayTextOperationTimeoutFiredEvent
        {
            CorrelationId = chunk.CorrelationId,
            Operation = NyxRelayTextOperationKind.Final,
            Sequence = 2,
            OperationGeneration = 3,
            Chunk = chunk.Clone(),
            CurrentPlatformMessageId = "relay-msg-1",
            CommandId = "llm-reply:corr-timeout-token",
            FinalText = "final text",
            LastFlushedText = "partial text",
            EditCount = 1,
            FiredAtUnixMs = 30,
        });

        var delivered = (await store.GetEventsAsync(agent.Id))
            .Select(e => e.EventData)
            .Where(e => e.Is(LlmReplyDeliveredEvent.Descriptor))
            .Select(e => e.Unpack<LlmReplyDeliveredEvent>())
            .OfType<LlmReplyDeliveredEvent>()
            .Should()
            .ContainSingle()
            .Subject;
        delivered.RunId.Should().Be("run-stream-final-timeout");
        agent.State.LastReplyDelivery.RunId.Should().Be("run-stream-final-timeout");
    }

    private static async Task<ConversationGAgent> CreateAgentAsync(
        string id,
        IActorRuntimeCallbackScheduler scheduler,
        IEventStore? store = null)
    {
        var eventStore = store ?? new InMemoryEventStore();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IActorDispatchPort, NoopActorDispatchPort>()
            .AddSingleton(scheduler)
            .AddSingleton<IConversationTurnRunner, SucceedingTurnRunner>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ConversationGAgent
        {
            Services = services,
            EventPublisher = new RecordingEventPublisher(),
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, id);
        await agent.ActivateAsync();
        return agent;
    }

    private static LlmReplyStreamChunkEvent CreateStreamChunk() =>
        new()
        {
            CorrelationId = "corr-timeout-token",
            RegistrationId = "reg-1",
            Activity = new ChatActivity
            {
                Id = "corr-timeout-token",
                Type = ActivityType.Message,
                ChannelId = new ChannelId { Value = "lark" },
                Bot = new BotInstanceId { Value = "lark-bot" },
                Conversation = new ConversationReference
                {
                    Channel = new ChannelId { Value = "lark" },
                    Bot = new BotInstanceId { Value = "lark-bot" },
                    Scope = ConversationScope.Group,
                    CanonicalKey = "conv:lark:grp",
                },
                Content = new MessageContent { Text = "user question" },
                OutboundDelivery = new OutboundDeliveryContext
                {
                    ReplyMessageId = "relay-msg-1",
                    CorrelationId = "corr-timeout-token",
                },
                TransportExtras = new TransportExtras
                {
                    NyxUserAccessToken = "runtime-user-access-token-secret",
                    NyxPlatform = "lark",
                    NyxConversationId = "oc_group_chat_1",
                },
            },
            AccumulatedText = "hello",
            ChunkAtUnixMs = 42,
            ReplyToken = "runtime-reply-token-secret",
            ReplyTokenExpiresAtUnixMs = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
        };

    private static void SetId(object agent, string id)
    {
        var current = agent.GetType();
        while (current is not null)
        {
            var setIdMethod = current.GetMethod(
                "SetId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (setIdMethod is not null)
            {
                setIdMethod.Invoke(agent, [id]);
                return;
            }

            current = current.BaseType;
        }

        throw new InvalidOperationException("Unable to set agent id via reflection.");
    }

    private sealed class SucceedingTurnRunner : IConversationTurnRunner
    {
        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", activity.Id));

        public Task<ConversationTurnResult> RunLlmReplyAsync(
            LlmReplyReadyEvent reply,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Sent(
                "sent",
                reply.Outbound?.Clone() ?? new MessageContent(),
                "bot"));

        public Task<ConversationTurnResult> RunContinueAsync(
            ConversationContinueRequestedEvent command,
            CancellationToken ct) =>
            Task.FromResult(ConversationTurnResult.Ignored("not-used", command.CommandId));

        public Task<ConversationStreamChunkResult> RunStreamChunkAsync(
            LlmReplyStreamChunkEvent chunk,
            string? currentPlatformMessageId,
            NyxRelayTextOperationKind operation,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct) =>
            Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId ?? "om_first"));
    }

    private sealed class RecordingCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public List<RuntimeCallbackTimeoutRequest> Timeouts { get; } = [];

        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Timeouts.Add(new RuntimeCallbackTimeoutRequest
            {
                ActorId = request.ActorId,
                CallbackId = request.CallbackId,
                TriggerEnvelope = request.TriggerEnvelope.Clone(),
                DueTime = request.DueTime,
                DeliveryMode = request.DeliveryMode,
            });
            return Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));
        }

        public Task<RuntimeCallbackLease> ScheduleTimerAsync(
            RuntimeCallbackTimerRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

        public Task CancelAsync(RuntimeCallbackLease lease, CancellationToken ct = default) => Task.CompletedTask;

        public Task PurgeActorAsync(string actorId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }

    private sealed class RecordingEventPublisher : IEventPublisher
    {
        public Task PublishAsync<TEvent>(
            TEvent evt,
            TopologyAudience audience = TopologyAudience.Children,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;

        public Task SendToAsync<TEvent>(
            string targetActorId,
            TEvent evt,
            CancellationToken ct = default,
            EventEnvelope? sourceEnvelope = null,
            EventEnvelopePublishOptions? options = null)
            where TEvent : IMessage =>
            Task.CompletedTask;
    }

    private sealed class InMemoryEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
            {
                stream = [];
                _events[agentId] = stream;
            }

            var currentVersion = stream.Count == 0 ? 0 : stream[^1].Version;
            if (currentVersion != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(
                    agentId,
                    expectedVersion,
                    currentVersion);

            var appended = events.Select(x => x.Clone()).ToList();
            stream.AddRange(appended);
            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = stream.Count == 0 ? 0 : stream[^1].Version,
                CommittedEvents = { appended.Select(x => x.Clone()) },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream))
                return Task.FromResult<IReadOnlyList<StateEvent>>([]);

            IReadOnlyList<StateEvent> result = fromVersion.HasValue
                ? stream.Where(x => x.Version > fromVersion.Value).Select(x => x.Clone()).ToList()
                : stream.Select(x => x.Clone()).ToList();
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var stream) || stream.Count == 0)
                return Task.FromResult(0L);
            return Task.FromResult(stream[^1].Version);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (toVersion <= 0 || !_events.TryGetValue(agentId, out var stream))
                return Task.FromResult(0L);

            var before = stream.Count;
            stream.RemoveAll(x => x.Version <= toVersion);
            return Task.FromResult((long)(before - stream.Count));
        }
    }

}
