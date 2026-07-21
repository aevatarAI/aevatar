using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Workflow.Abstractions;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class ChatTurnHistoryDeliveryGAgentTests
{
    private const string DeliveryActorId = "chat-history-delivery:actor-address-alpha";
    private const string DeliveryId = "chat-history-delivery-business-alpha";
    private const string WorkflowActorId = "workflow-actor";
    private const string WorkflowCommandId = "workflow-command";

    [Fact]
    public async Task TerminalNotification_ShouldAppendFromWorkflowOutboxWithoutProjectionAttachment()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(runtime, dispatch);

        await agent.HandleEventAsync(Envelope(Reserve(createConversationIfMissing: true), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Bind(), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Terminal(), WorkflowActorId));

        runtime.CreateCalls.Should().ContainSingle()
            .Which.Should().Be(ChatHistoryActorIds.Conversation("scope-a", "conversation-a"));
        dispatch.Calls.Should().ContainSingle();
        var append = dispatch.Calls.Single().Envelope.Payload.Unpack<AppendChatTurnCommand>();
        append.ScopeId.Should().Be("scope-a");
        append.ConversationId.Should().Be("conversation-a");
        append.DeliveryActorId.Should().Be(DeliveryActorId);
        append.Turn.TurnId.Should().Be("turn-a");
        append.Turn.UserText.Should().Be("original user text");
        append.Turn.AssistantText.Should().Be("terminal output");
        append.Turn.TerminalStatus.Should().Be(ChatTurnTerminalStatus.Completed);
    }

    [Fact]
    public async Task TerminalNotification_ShouldNotCreateOrAppend_WhenContinueConversationIsMissing()
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(runtime, dispatch);

        await agent.HandleEventAsync(Envelope(Reserve(createConversationIfMissing: false), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Bind(), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Terminal(), WorkflowActorId));

        runtime.CreateCalls.Should().BeEmpty();
        dispatch.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, ChatTurnHistoryDeliveryStatus.AppendCommitted)]
    [InlineData(false, ChatTurnHistoryDeliveryStatus.AppendRejected)]
    public async Task Reserve_ShouldNotMutateDelivery_WhenAppendResultIsTerminal(
        bool appendAccepted,
        ChatTurnHistoryDeliveryStatus expectedStatus)
    {
        var runtime = new RecordingActorRuntime();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(runtime, dispatch);

        await agent.HandleEventAsync(Envelope(Reserve(createConversationIfMissing: true), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Bind(), "chat-history-terminal-delivery-port"));
        await agent.HandleEventAsync(Envelope(Terminal(), WorkflowActorId));
        await agent.HandleEventAsync(Envelope(AppendResult(appendAccepted), "chat-conversation"));

        agent.State.Status.Should().Be(expectedStatus);

        await agent.HandleEventAsync(Envelope(
            Reserve(
                createConversationIfMissing: true,
                workflowActorId: "workflow-actor-retry",
                requestFingerprint: "fingerprint-retry"),
            "chat-history-terminal-delivery-port"));

        agent.State.Status.Should().Be(expectedStatus);
        agent.State.WorkflowActorId.Should().Be(WorkflowActorId);
        agent.State.RequestFingerprint.Should().Be("fingerprint-original");
    }

    private static ChatTurnHistoryDeliveryReserveRequested Reserve(
        bool createConversationIfMissing,
        string workflowActorId = WorkflowActorId,
        string requestFingerprint = "fingerprint-original") => new()
    {
        DeliveryId = DeliveryId,
        ScopeId = "scope-a",
        ConversationId = "conversation-a",
        TurnId = "turn-a",
        UserText = "original user text",
        WorkflowActorId = workflowActorId,
        WorkflowCommandId = WorkflowCommandId,
        WorkflowCorrelationId = "workflow-correlation",
        CreateConversationIfMissing = createConversationIfMissing,
        RequestFingerprint = requestFingerprint,
    };

    private static ChatTurnHistoryDeliveryAcceptedBound Bind() => new()
    {
        DeliveryId = DeliveryId,
        WorkflowActorId = WorkflowActorId,
        WorkflowCommandId = WorkflowCommandId,
        WorkflowCorrelationId = "workflow-correlation",
    };

    private static WorkflowRunTerminalNotification Terminal() => new()
    {
        DeliveryId = DeliveryId,
        WorkflowActorId = WorkflowActorId,
        WorkflowRunId = "workflow-run",
        WorkflowCommandId = WorkflowCommandId,
        WorkflowCorrelationId = "workflow-correlation",
        Status = WorkflowRunTerminalStatus.Completed,
        Output = " terminal output ",
        TerminalAt = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-16T00:00:00Z")),
    };

    private static ChatTurnHistoryDeliveryAppendResultObserved AppendResult(bool accepted) => new()
    {
        DeliveryActorId = DeliveryActorId,
        ConversationId = "conversation-a",
        TurnId = "turn-a",
        Accepted = accepted,
        RejectionReason = accepted
            ? ChatTurnAppendRejectionReason.Unspecified
            : ChatTurnAppendRejectionReason.Conflict,
        ObservedAtUnixMs = DateTimeOffset.Parse("2026-07-16T00:00:01Z").ToUnixTimeMilliseconds(),
    };

    private static EventEnvelope Envelope(IMessage payload, string publisherActorId) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect(publisherActorId, DeliveryActorId),
        };

    private static async Task<ChatTurnHistoryDeliveryGAgent> CreateAgentAsync(
        RecordingActorRuntime runtime,
        RecordingActorDispatchPort dispatch)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, RecordingEventStore>()
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ChatTurnHistoryDeliveryGAgent(
            runtime,
            dispatch)
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChatTurnHistoryDeliveryState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [DeliveryActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private sealed class RecordingActorRuntime : IActorRuntime
    {
        public List<string> CreateCalls { get; } = [];

        public Task<IActor> CreateAsync<TAgent>(string? id = null, CancellationToken ct = default)
            where TAgent : IAgent
        {
            ct.ThrowIfCancellationRequested();
            CreateCalls.Add(id ?? string.Empty);
            return Task.FromResult<IActor>(new RecordingActor(id ?? string.Empty));
        }

        public Task<IActor> CreateAsync(System.Type agentType, string? id = null, CancellationToken ct = default) =>
            CreateAsync<NoopAgent>(id, ct);

        public Task DestroyAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IActor?> GetAsync(string id) => Task.FromResult<IActor?>(null);

        public Task<bool> ExistsAsync(string id) => Task.FromResult(false);

        public Task LinkAsync(string parentId, string childId, CancellationToken ct = default) => Task.CompletedTask;

        public Task UnlinkAsync(string childId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingActorDispatchPort : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope));
            return Task.FromResult(DispatchAdmissionFactory.Create(actorId, envelope));
        }
    }

    private sealed class RecordingEventStore : IEventStore
    {
        private readonly Dictionary<string, List<StateEvent>> _events = new(StringComparer.Ordinal);

        public Task<EventStoreCommitResult> AppendAsync(
            string agentId,
            IEnumerable<StateEvent> events,
            long expectedVersion,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var existing))
            {
                existing = [];
                _events[agentId] = existing;
            }

            if (existing.Count != expectedVersion)
                throw new EventStoreOptimisticConcurrencyException(agentId, expectedVersion, existing.Count);

            var committed = new List<StateEvent>();
            foreach (var stateEvent in events)
            {
                var copy = stateEvent.Clone();
                copy.Version = existing.Count + 1;
                existing.Add(copy);
                committed.Add(copy.Clone());
            }

            return Task.FromResult(new EventStoreCommitResult
            {
                AgentId = agentId,
                LatestVersion = existing.Count,
                CommittedEvents = { committed },
            });
        }

        public Task<IReadOnlyList<StateEvent>> GetEventsAsync(
            string agentId,
            long? fromVersion = null,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<StateEvent> result = _events.TryGetValue(agentId, out var events)
                ? events
                    .Where(e => fromVersion is null || e.Version >= fromVersion)
                    .Select(e => e.Clone())
                    .ToList()
                : [];
            return Task.FromResult(result);
        }

        public Task<long> GetVersionAsync(string agentId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_events.TryGetValue(agentId, out var events) ? (long)events.Count : 0);
        }

        public Task<long> DeleteEventsUpToAsync(string agentId, long toVersion, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (!_events.TryGetValue(agentId, out var events))
                return Task.FromResult(0L);

            var deleted = events.RemoveAll(e => e.Version <= toVersion);
            return Task.FromResult((long)deleted);
        }
    }

    private sealed class RecordingActor(string id) : IActor
    {
        public string Id { get; } = id;
        public IAgent Agent { get; } = new NoopAgent();
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetParentIdAsync() => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> GetChildrenIdsAsync() => Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class NoopAgent : IAgent
    {
        public string Id => "noop";
        public Task HandleEventAsync(EventEnvelope envelope, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetDescriptionAsync() => Task.FromResult("noop");
        public Task<IReadOnlyList<System.Type>> GetSubscribedEventTypesAsync() =>
            Task.FromResult<IReadOnlyList<System.Type>>([]);
        public Task ActivateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task DeactivateAsync(CancellationToken ct = default) => Task.CompletedTask;
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
}
