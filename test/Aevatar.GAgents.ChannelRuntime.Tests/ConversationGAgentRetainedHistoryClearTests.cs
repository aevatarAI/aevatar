using System.Reflection;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChannelRuntime.Tests;

// /clear ownership: the turn runner only reports the typed
// RetainedHistoryClearRequested outcome; ConversationGAgent — the sole owner of the
// retained transcript window — commits ConversationRetainedHistoryClearedEvent and
// resets the window inside its own event handling.
public sealed class ConversationGAgentRetainedHistoryClearTests
{
    [Fact]
    public async Task HandleInboundActivityAsync_WhenRunnerRequestsHistoryClear_ResetsRetainedHistory()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:dm:ou_user_1");
        var eventStore = new InMemoryEventStore();
        var runner = new SeedThenClearTurnRunner();
        var agent = await CreateAgentAsync(actorId, runner, eventStore);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-seed-1", "建一个定时任务"));
        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "msg-seed-1",
            RunId = "agent-run-seed-1",
            Activity = BuildInboundActivity("msg-seed-1", "建一个定时任务"),
            Outbound = new MessageContent { Text = "好的" },
            TerminalState = LlmReplyTerminalState.Completed,
            AppendedHistory =
            {
                new ConversationHistoryEntry { Role = "user", Content = "建一个定时任务" },
                new ConversationHistoryEntry { Role = "assistant", Content = "好的" },
            },
        });
        agent.State.RetainedHistory.Should().NotBeEmpty("the seeding turn must retain history first");

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-clear-1", "/clear"));

        agent.State.RetainedHistory.Should().BeEmpty(
            "the clear-requesting turn must reset the retained transcript window");
        agent.State.RecentAttachmentActivities.Should().BeEmpty();
        var events = await eventStore.GetEventsAsync(actorId);
        events.Should().Contain(
            x => x.EventData.TypeUrl.EndsWith(
                ConversationRetainedHistoryClearedEvent.Descriptor.FullName,
                StringComparison.Ordinal),
            "the clear must be a committed, replayable domain event");
        var cleared = events
            .Where(x => x.EventData.TypeUrl.EndsWith(
                ConversationRetainedHistoryClearedEvent.Descriptor.FullName,
                StringComparison.Ordinal))
            .Select(x => x.EventData.Unpack<ConversationRetainedHistoryClearedEvent>())
            .Single();
        cleared.ClearedEntryCount.Should().Be(2);
        cleared.ProcessedActivityId.Should().Be("msg-clear-1");
    }

    [Fact]
    public async Task HandleInboundActivityAsync_WhenTurnDoesNotRequestClear_KeepsRetainedHistory()
    {
        var actorId = ConversationGAgent.BuildActorId("lark:dm:ou_user_1");
        var eventStore = new InMemoryEventStore();
        var runner = new SeedThenClearTurnRunner { ClearOnSecondTurn = false };
        var agent = await CreateAgentAsync(actorId, runner, eventStore);

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-seed-1", "建一个定时任务"));
        await agent.HandleLlmReplyReadyAsync(new LlmReplyReadyEvent
        {
            CorrelationId = "msg-seed-1",
            RunId = "agent-run-seed-1",
            Activity = BuildInboundActivity("msg-seed-1", "建一个定时任务"),
            Outbound = new MessageContent { Text = "好的" },
            TerminalState = LlmReplyTerminalState.Completed,
            AppendedHistory =
            {
                new ConversationHistoryEntry { Role = "assistant", Content = "好的" },
            },
        });

        await agent.HandleInboundActivityAsync(BuildInboundActivity("msg-plain-1", "谢谢"));

        agent.State.RetainedHistory.Should().NotBeEmpty(
            "ordinary turns must not reset the retained transcript window");
        var events = await eventStore.GetEventsAsync(actorId);
        events.Should().NotContain(
            x => x.EventData.TypeUrl.EndsWith(
                ConversationRetainedHistoryClearedEvent.Descriptor.FullName,
                StringComparison.Ordinal));
    }

    private static async Task<ConversationGAgent> CreateAgentAsync(
        string id,
        IConversationTurnRunner runner,
        InMemoryEventStore eventStore)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
            .AddSingleton<IActorDispatchPort, NoopActorDispatchPort>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddSingleton(runner)
            .AddSingleton<IChannelLlmReplyRunDispatcher, RecordingLlmReplyRunDispatcher>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ConversationGAgent
        {
            Services = services,
            EventPublisher = new NoopEventPublisher(),
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ConversationGAgentState>>(),
        };
        SetId(agent, id);
        await agent.ActivateAsync();
        return agent;
    }

    private static ChatActivity BuildInboundActivity(string messageId, string text) =>
        new()
        {
            Id = messageId,
            Type = ActivityType.Message,
            ChannelId = ChannelId.From("lark"),
            Bot = BotInstanceId.From("reg-1"),
            Conversation = ConversationReference.Create(
                ChannelId.From("lark"),
                BotInstanceId.From("reg-1"),
                ConversationScope.DirectMessage,
                "ou_user_1",
                "dm",
                "ou_user_1"),
            From = new ParticipantRef { CanonicalId = "ou_user_1" },
            Content = new MessageContent { Text = text },
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

    // First inbound turn defers to an LLM reply (seeding retained history through the
    // ready event); the second inbound turn reports the /clear outcome (or a plain
    // sent turn when ClearOnSecondTurn is false).
    private sealed class SeedThenClearTurnRunner : IConversationTurnRunner
    {
        private int _inboundTurns;

        public bool ClearOnSecondTurn { get; init; } = true;

        public Task<ConversationTurnResult> RunInboundAsync(
            ChatActivity activity,
            ConversationTurnRuntimeContext runtimeContext,
            CancellationToken ct)
        {
            _inboundTurns++;
            if (_inboundTurns == 1)
            {
                return Task.FromResult(ConversationTurnResult.LlmReplyRequested(new NeedsLlmReplyEvent
                {
                    CorrelationId = activity.Id,
                    RunId = "agent-run-seed-1",
                    RegistrationId = "reg-1",
                    Activity = activity.Clone(),
                    RequestedAtUnixMs = 10,
                }));
            }

            var sent = ConversationTurnResult.Sent(
                $"sent:{activity.Id}",
                new MessageContent { Text = "✅ 已清空本会话的对话记忆" },
                "bot");
            return Task.FromResult(ClearOnSecondTurn
                ? sent with { RetainedHistoryClearRequested = true }
                : sent);
        }

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
            Task.FromResult(ConversationStreamChunkResult.Succeeded(currentPlatformMessageId));
    }

    private sealed class RecordingLlmReplyRunDispatcher : IChannelLlmReplyRunDispatcher
    {
        public Task DispatchAsync(NeedsLlmReplyEvent request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class NoopActorDispatchPort : IActorDispatchPort
    {
        public Task<DispatchAdmission> DispatchAsync(string actorId, EventEnvelope envelope, CancellationToken ct = default) =>
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

    private sealed class NoopEventPublisher : IEventPublisher
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
}
