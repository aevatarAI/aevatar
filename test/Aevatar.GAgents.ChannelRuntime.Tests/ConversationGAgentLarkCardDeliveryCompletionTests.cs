using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

public sealed class ConversationGAgentLarkCardDeliveryCompletionTests
{
    [Fact]
    public async Task CompletionSuccess_PersistsDeliveredBeforeTurnCompleted_AndClearsLifecycleIdempotently()
    {
        var store = new InMemoryEventStore();
        var publisher = new SelfHandlingEventPublisher();
        var agent = await CreateAgentAsync("conversation-lark-card-success", store, publisher);
        SeedReplyLifecycle(agent, "corr-card-success");
        var completion = CreateCompletion("corr-card-success");

        await publisher.SendToAsync(agent.Id, completion);
        await publisher.SendToAsync(agent.Id, completion.Clone());

        var events = await store.GetEventsAsync(agent.Id);
        var deliveredIndex = FindEventIndex(events, LlmReplyDeliveredEvent.Descriptor.FullName);
        var completedIndex = FindEventIndex(events, ConversationTurnCompletedEvent.Descriptor.FullName);
        deliveredIndex.Should().BeGreaterThanOrEqualTo(0);
        completedIndex.Should().BeGreaterThan(deliveredIndex);
        events.Should().ContainSingle(e => e.EventData.Is(LlmReplyDeliveredEvent.Descriptor));
        events.Should().ContainSingle(e => e.EventData.Is(DeliveryProducedEvent.Descriptor));
        events.Should().ContainSingle(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor));
        events.Should().ContainSingle(e => e.EventData.Is(ConversationReplyLifecycleClearedEvent.Descriptor));

        var delivered = events[deliveredIndex].EventData.Unpack<LlmReplyDeliveredEvent>();
        delivered.CorrelationId.Should().Be("corr-card-success");
        delivered.RunId.Should().Be("run-card-success");
        delivered.AckedAtUnixMs.Should().Be(123456);
        delivered.ChannelMessageId.Should().Be("lark-card-stream:om-card-success");

        var deliveryRecord = events.Single(e => e.EventData.Is(DeliveryProducedEvent.Descriptor));
        var delivery = deliveryRecord.EventData.Unpack<DeliveryProducedEvent>();
        delivery.RunId.Should().Be("run-card-success");
        delivery.TurnId.Should().Be("corr-card-success");
        delivery.DeliveryKind.Should().Be(DeliveryKind.StreamingCard);
        delivery.Status.Should().Be(DeliveryStatus.Succeeded);
        delivery.ProducedAtVersion.Should().Be(deliveryRecord.Version);
        delivery.RequestId.Should().Be("llm:corr-card-success");
        delivery.SourceEventId.Should().Be("corr-card-success");
        delivery.ProviderMessageId.Should().Be("lark-card-stream:om-card-success");
        delivery.CardId.Should().BeEmpty();
        delivery.Target.Channel.Value.Should().Be("lark");
        delivery.Target.ConversationKey.Should().Be("conv:lark:grp");
        delivery.Target.Platform.Should().Be("lark");
        delivery.Target.AddressId.Should().Be("relay-msg-success");
        delivery.Target.AddressType.Should().BeEmpty();
        delivery.Target.ConversationId.Should().Be("conv:lark:grp");
        delivery.Target.ReplyMessageId.Should().Be("relay-msg-success");

        var completed = events[completedIndex].EventData.Unpack<ConversationTurnCompletedEvent>();
        completed.CausationCommandId.Should().Be("llm:corr-card-success");
        completed.SentActivityId.Should().Be("lark-card-stream:om-card-success");
        completed.Outbound.Text.Should().Be("final card text");
        completed.CompletedAtUnixMs.Should().Be(123456);
        completed.Conversation.CanonicalKey.Should().Be("conv:lark:grp");
        completed.AppendedHistory.Select(entry => entry.Content).Should().Equal("user asked", "bot answered");
        completed.OutboundDelivery.ReplyMessageId.Should().Be("relay-msg-success");

        agent.State.ProcessedCommandIds.Should().Contain("llm:corr-card-success");
        agent.State.RetainedHistory.Select(entry => entry.Content).Should().Equal("user asked", "bot answered");
        agent.State.ActiveReplyLifecycles.Should().BeEmpty();
        agent.State.LastReplyDelivery.RunId.Should().Be("run-card-success");
        agent.State.LastReplyDelivery.Delivered.ChannelMessageId.Should().Be("lark-card-stream:om-card-success");
        agent.State.RecentDeliveries.Should().ContainSingle();
        agent.State.RecentDeliveries[0].RequestId.Should().Be("llm:corr-card-success");
        agent.State.RecentDeliveries[0].Status.Should().Be(DeliveryStatus.Succeeded);
        agent.State.RecentDeliveries[0].ProviderMessageId.Should().Be("lark-card-stream:om-card-success");
        agent.State.LastSuccessfulDelivery.Should().NotBeNull();
        agent.State.LastSuccessfulDelivery!.RequestId.Should().Be("llm:corr-card-success");
        agent.State.LastSuccessfulDelivery.ProviderMessageId.Should().Be("lark-card-stream:om-card-success");
    }

    [Fact]
    public async Task CompletionFailure_PersistsDeliveryFailedBeforeTurnCompleted_WithFailureFields()
    {
        var store = new InMemoryEventStore();
        var publisher = new SelfHandlingEventPublisher();
        var agent = await CreateAgentAsync("conversation-lark-card-failure", store, publisher);
        SeedReplyLifecycle(agent, "corr-card-failure");
        var completion = CreateCompletion("corr-card-failure", deliveryFailure: new LlmReplyDeliveryFailedEvent
        {
            ErrorCode = "card_finalize_failed",
            ErrorMessage = "finalize rejected",
        });

        await publisher.SendToAsync(agent.Id, completion);

        var events = await store.GetEventsAsync(agent.Id);
        var failedIndex = FindEventIndex(events, LlmReplyDeliveryFailedEvent.Descriptor.FullName);
        var completedIndex = FindEventIndex(events, ConversationTurnCompletedEvent.Descriptor.FullName);
        failedIndex.Should().BeGreaterThanOrEqualTo(0);
        completedIndex.Should().BeGreaterThan(failedIndex);

        var failed = events[failedIndex].EventData.Unpack<LlmReplyDeliveryFailedEvent>();
        failed.CorrelationId.Should().Be("corr-card-failure");
        failed.RunId.Should().Be("run-card-failure");
        failed.FailedAtUnixMs.Should().Be(123456);
        failed.ErrorCode.Should().Be("card_finalize_failed");
        failed.ErrorMessage.Should().Be("finalize rejected");

        var completed = events[completedIndex].EventData.Unpack<ConversationTurnCompletedEvent>();
        completed.CausationCommandId.Should().Be("llm:corr-card-failure");
        completed.SentActivityId.Should().Be("lark-card-stream:om-card-failure");
        completed.Outbound.Text.Should().Be("final card text");
        completed.AppendedHistory.Select(entry => entry.Content).Should().Equal("user asked", "bot answered");

        agent.State.ActiveReplyLifecycles.Should().BeEmpty();
        agent.State.LastReplyDelivery.RunId.Should().Be("run-card-failure");
        agent.State.LastReplyDelivery.Failed.ErrorCode.Should().Be("card_finalize_failed");
        events.Should().ContainSingle(e => e.EventData.Is(LlmReplyDeliveryFailedEvent.Descriptor));
        events.Should().ContainSingle(e => e.EventData.Is(DeliveryProducedEvent.Descriptor));
        events.Should().ContainSingle(e => e.EventData.Is(ConversationTurnCompletedEvent.Descriptor));
        var deliveryRecord = events.Single(e => e.EventData.Is(DeliveryProducedEvent.Descriptor));
        var delivery = deliveryRecord.EventData.Unpack<DeliveryProducedEvent>();
        delivery.RunId.Should().Be("run-card-failure");
        delivery.TurnId.Should().Be("corr-card-failure");
        delivery.DeliveryKind.Should().Be(DeliveryKind.StreamingCard);
        delivery.Status.Should().Be(DeliveryStatus.FailedPostSend);
        delivery.ProducedAtVersion.Should().Be(deliveryRecord.Version);
        delivery.RequestId.Should().Be("llm:corr-card-failure");
        delivery.SourceEventId.Should().Be("corr-card-failure");
        delivery.ProviderMessageId.Should().Be("lark-card-stream:om-card-failure");
        delivery.CardId.Should().BeEmpty();
        delivery.Target.Channel.Value.Should().Be("lark");
        delivery.Target.ConversationKey.Should().Be("conv:lark:grp");
        delivery.Target.Platform.Should().Be("lark");
        delivery.Target.AddressId.Should().Be("relay-msg-failure");
        delivery.Target.AddressType.Should().BeEmpty();
        delivery.Target.ConversationId.Should().Be("conv:lark:grp");
        delivery.Target.ReplyMessageId.Should().Be("relay-msg-failure");
        agent.State.RecentDeliveries.Should().ContainSingle();
        agent.State.RecentDeliveries[0].RequestId.Should().Be("llm:corr-card-failure");
        agent.State.RecentDeliveries[0].Status.Should().Be(DeliveryStatus.FailedPostSend);
        agent.State.LastSuccessfulDelivery.Should().BeNull();
    }

    private static async Task<ConversationGAgent> CreateAgentAsync(
        string id,
        IEventStore store,
        SelfHandlingEventPublisher publisher,
        IConversationTurnRunner? runner = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton<IActorDispatchPort, NoopActorDispatchPort>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddSingleton(runner ?? new RecordingTurnRunner())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ConversationGAgent
        {
            Services = services,
            EventPublisher = publisher,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, id);
        publisher.SelfTarget = agent;
        await agent.ActivateAsync();
        return agent;
    }

    private static void SeedReplyLifecycle(ConversationGAgent agent, string correlationId)
    {
        agent.State.ActiveReplyLifecycles.Add(new ConversationReplyLifecycleState
        {
            CorrelationId = correlationId,
            Mode = ConversationReplyLifecycleMode.NyxRelayText,
            Phase = ConversationReplyLifecyclePhase.TextStreaming,
            PlatformMessageId = "relay-msg-" + correlationId,
            LastFlushedText = "partial",
            UpdatedAtUnixMs = 100,
        });
    }

    private static LarkCardDeliveryCompletedEvent CreateCompletion(
        string correlationId,
        LlmReplyDeliveryFailedEvent? deliveryFailure = null)
    {
        var suffix = correlationId.Replace("corr-card-", string.Empty, StringComparison.Ordinal);
        var completion = new LarkCardDeliveryCompletedEvent
        {
            CorrelationId = correlationId,
            RunId = "run-card-" + suffix,
            CommandId = "llm:" + correlationId,
            Activity = CreateActivity(correlationId, suffix),
            CardMessageId = "om-card-" + suffix,
            OutboundText = "final card text",
            CompletedAtUnixMs = 123456,
        };
        completion.AppendedHistory.AddRange(
        [
            new ConversationHistoryEntry
            {
                Role = "user",
                Content = "user asked",
            },
            new ConversationHistoryEntry
            {
                Role = "assistant",
                Content = "bot answered",
            },
        ]);
        if (deliveryFailure is not null)
            completion.DeliveryFailure = deliveryFailure.Clone();
        return completion;
    }

    private static ChatActivity CreateActivity(string correlationId, string suffix) => new()
    {
        Id = "activity-" + suffix,
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
            CorrelationId = correlationId,
            ReplyMessageId = "relay-msg-" + suffix,
        },
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

    private static int FindEventIndex(IReadOnlyList<StateEvent> events, string eventType)
    {
        for (var i = 0; i < events.Count; i++)
        {
            if (string.Equals(events[i].EventType, eventType, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private sealed class RecordingTurnRunner : IConversationTurnRunner
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
            Task.FromResult(ConversationTurnResult.Ignored("not-used", reply.CorrelationId));

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
            Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId ?? "om"));

        public Task OnReplyDeliveredAsync(ChatActivity activity, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class SelfHandlingEventPublisher : IEventPublisher
    {
        public ConversationGAgent? SelfTarget { get; set; }

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
            where TEvent : IMessage
        {
            return SelfTarget is not null &&
                   string.Equals(targetActorId, SelfTarget.Id, StringComparison.Ordinal)
                ? SelfTarget.HandleEventAsync(Envelope(targetActorId, evt), ct)
                : Task.CompletedTask;
        }
    }

    private sealed class NoopActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default) =>
            Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
    }

    private sealed class NoopCallbackScheduler : IActorRuntimeCallbackScheduler
    {
        public Task<RuntimeCallbackLease> ScheduleTimeoutAsync(
            RuntimeCallbackTimeoutRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new RuntimeCallbackLease(
                request.ActorId,
                request.CallbackId,
                1,
                RuntimeCallbackBackend.InMemory));

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
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, currentVersion);

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

    private static EventEnvelope Envelope(string actorId, IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", actorId),
            Propagation = new EnvelopePropagation
            {
                CorrelationId = payload switch
                {
                    LarkCardDeliveryCompletedEvent completed => completed.CorrelationId,
                    _ => string.Empty,
                },
            },
        };
}
