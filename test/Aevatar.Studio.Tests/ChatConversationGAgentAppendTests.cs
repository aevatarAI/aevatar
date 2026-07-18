using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class ChatConversationGAgentAppendTests
{
    private const string ActorId = "chat-scope-a-conversation-a";

    [Fact]
    public async Task AppendChatTurnCommand_ShouldAppendSingleTerminalTurnWithoutReplacingTranscript()
    {
        var agent = await CreateAgentAsync();

        await agent.HandleEventAsync(Envelope(CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed)));
        await agent.HandleEventAsync(Envelope(CreateAppend("turn-2", "next", "done", ChatTurnTerminalStatus.Failed)));

        agent.State.Turns.Should().HaveCount(2);
        agent.State.Turns[0].TurnId.Should().Be("turn-1");
        agent.State.Turns[0].Sequence.Should().Be(1);
        agent.State.Turns[0].UserText.Should().Be("hello");
        agent.State.Turns[0].AssistantText.Should().Be("hi");
        agent.State.Turns[0].TerminalStatus.Should().Be(ChatTurnTerminalStatus.Completed);
        agent.State.Turns[1].TurnId.Should().Be("turn-2");
        agent.State.Turns[1].Sequence.Should().Be(2);
        agent.State.Turns[1].TerminalStatus.Should().Be(ChatTurnTerminalStatus.Failed);
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldSynthesizeTitleFromFirstUserText_WhenTitleIsMissing()
    {
        var agent = await CreateAgentAsync();

        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-1",
            "  Please verify\n\nchat history\tlist index mapping  ",
            "done",
            ChatTurnTerminalStatus.Completed)));

        agent.State.Title.Should().Be("Please verify chat history list index mapping");
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldPreferExplicitTitleOverSynthesizedTitle()
    {
        var agent = await CreateAgentAsync();
        var command = CreateAppend(
            "turn-1",
            "user text should not become title",
            "done",
            ChatTurnTerminalStatus.Completed);
        command.Title = "Explicit title";

        await agent.HandleEventAsync(Envelope(command));

        agent.State.Title.Should().Be("Explicit title");
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldNotReplaceExistingTitleFromLaterUserText()
    {
        var agent = await CreateAgentAsync();

        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-1",
            "first user title",
            "done",
            ChatTurnTerminalStatus.Completed)));
        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-2",
            "second user text should not replace title",
            "done again",
            ChatTurnTerminalStatus.Completed)));

        agent.State.Title.Should().Be("first user title");
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldTruncateSynthesizedTitleToStableDisplayLength()
    {
        var agent = await CreateAgentAsync();

        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-1",
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ",
            "done",
            ChatTurnTerminalStatus.Completed)));

        agent.State.Title.Should().Be("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTU…");
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldDeduplicateSameTurnPayload()
    {
        var agent = await CreateAgentAsync();
        var command = CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed);

        await agent.HandleEventAsync(Envelope(command));
        await agent.HandleEventAsync(Envelope(command.Clone()));

        agent.State.Turns.Should().ContainSingle();
        agent.State.Turns[0].Sequence.Should().Be(1);
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldRejectSameTurnIdWithDifferentPayload()
    {
        var agent = await CreateAgentAsync();

        await agent.HandleEventAsync(Envelope(CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed)));
        await agent.HandleEventAsync(Envelope(CreateAppend("turn-1", "hello", "changed", ChatTurnTerminalStatus.Completed)));

        agent.State.Turns.Should().ContainSingle();
        agent.State.Turns[0].AssistantText.Should().Be("hi");
        agent.State.LastRejectedAppend.Should().NotBeNull();
        agent.State.LastRejectedAppend!.Reason.Should().Be(ChatTurnAppendRejectionReason.Conflict);
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldRejectTurnAfterMaxTurnsWithoutTrimmingExistingTurns()
    {
        var agent = await CreateAgentAsync();
        for (var i = 1; i <= ChatConversationGAgent.MaxTurns; i++)
        {
            await agent.HandleEventAsync(Envelope(CreateAppend($"turn-{i}", $"user-{i}", $"assistant-{i}", ChatTurnTerminalStatus.Completed)));
        }

        await agent.HandleEventAsync(Envelope(CreateAppend("turn-251", "overflow", "overflow", ChatTurnTerminalStatus.Completed)));

        agent.State.Turns.Should().HaveCount(ChatConversationGAgent.MaxTurns);
        agent.State.Turns[0].TurnId.Should().Be("turn-1");
        agent.State.Turns[^1].TurnId.Should().Be("turn-250");
        agent.State.LastRejectedAppend.Should().NotBeNull();
        agent.State.LastRejectedAppend!.Reason.Should().Be(ChatTurnAppendRejectionReason.MaxTurnsExceeded);
    }

    [Fact]
    public async Task AppendChatTurnCommand_WhenRejectedWithDeliveryActor_ShouldDispatchAppendResult()
    {
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch);
        for (var i = 1; i <= ChatConversationGAgent.MaxTurns; i++)
        {
            await agent.HandleEventAsync(Envelope(CreateAppend($"turn-{i}", $"user-{i}", $"assistant-{i}", ChatTurnTerminalStatus.Completed)));
        }

        var overflow = CreateAppend("turn-251", "overflow", "overflow", ChatTurnTerminalStatus.Completed);
        overflow.DeliveryActorId = "delivery-actor";

        await agent.HandleEventAsync(Envelope(overflow));

        dispatch.Calls.Should().ContainSingle();
        var call = dispatch.Calls.Single();
        call.ActorId.Should().Be("delivery-actor");
        var result = call.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryAppendResultObserved>();
        result.DeliveryActorId.Should().Be("delivery-actor");
        result.ConversationId.Should().Be("conversation-a");
        result.TurnId.Should().Be("turn-251");
        result.Accepted.Should().BeFalse();
        result.RejectionReason.Should().Be(ChatTurnAppendRejectionReason.MaxTurnsExceeded);
    }

    [Fact]
    public async Task ContinuationAdmissionRequested_ShouldReturn_WhenConversationStateMatchesRequest()
    {
        var agent = await CreateAgentAsync();
        await agent.HandleEventAsync(Envelope(CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed)));

        var act = () => agent.HandleEventAsync(Envelope(new ChatConversationContinuationAdmissionRequested
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
        }));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ContinuationAdmissionRequested_ShouldThrowNotFound_WhenConversationStateIsNotContinuable()
    {
        var missingAgent = await CreateAgentAsync();
        var wrongScopeAgent = await CreateAgentAsync();
        var wrongConversationAgent = await CreateAgentAsync();
        var deletedAgent = await CreateAgentAsync();
        await wrongScopeAgent.HandleEventAsync(Envelope(CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed)));
        await wrongConversationAgent.HandleEventAsync(Envelope(CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed)));
        await deletedAgent.HandleEventAsync(Envelope(CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed)));
        await deletedAgent.HandleEventAsync(Envelope(new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
        }));

        var missingAct = () => missingAgent.HandleEventAsync(Envelope(ContinuationAdmission("scope-a", "conversation-a")));
        var wrongScopeAct = () => wrongScopeAgent.HandleEventAsync(Envelope(ContinuationAdmission("scope-b", "conversation-a")));
        var wrongConversationAct = () => wrongConversationAgent.HandleEventAsync(Envelope(ContinuationAdmission("scope-a", "conversation-b")));
        var deletedAct = () => deletedAgent.HandleEventAsync(Envelope(ContinuationAdmission("scope-a", "conversation-a")));

        await missingAct.Should().ThrowAsync<ChatConversationContinuationAdmissionNotFoundException>();
        await wrongScopeAct.Should().ThrowAsync<ChatConversationContinuationAdmissionNotFoundException>();
        await wrongConversationAct.Should().ThrowAsync<ChatConversationContinuationAdmissionNotFoundException>();
        await deletedAct.Should().ThrowAsync<ChatConversationContinuationAdmissionNotFoundException>();
    }

    private static AppendChatTurnCommand CreateAppend(
        string turnId,
        string userText,
        string assistantText,
        ChatTurnTerminalStatus terminalStatus) =>
        new()
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            Turn = new ChatTurn
            {
                TurnId = turnId,
                UserText = userText,
                AssistantText = assistantText,
                TerminalStatus = terminalStatus,
                TerminalTime = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-16T00:00:00Z")),
                LlmRoute = "route-a",
                LlmModel = "model-a",
            },
        };

    private static ChatConversationContinuationAdmissionRequested ContinuationAdmission(
        string scopeId,
        string conversationId) =>
        new()
        {
            ScopeId = scopeId,
            ConversationId = conversationId,
        };

    private static EventEnvelope Envelope(Google.Protobuf.IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", ActorId),
        };

    private static async Task<ChatConversationGAgent> CreateAgentAsync(
        IActorDispatchPort? dispatchPort = null)
    {
        var services = new ServiceCollection()
            .AddSingleton<IEventStore, RecordingEventStore>()
            .AddSingleton(dispatchPort ?? new RecordingActorDispatchPort())
            .AddSingleton<EventSourcingRuntimeOptions>()
            .AddSingleton<IActorRuntimeCallbackScheduler, NoopCallbackScheduler>()
            .AddTransient(typeof(IEventSourcingBehaviorFactory<>), typeof(DefaultEventSourcingBehaviorFactory<>))
            .BuildServiceProvider();

        var agent = new ChatConversationGAgent
        {
            Services = services,
            EventSourcingBehaviorFactory =
                services.GetRequiredService<IEventSourcingBehaviorFactory<ChatConversationState>>(),
        };
        typeof(Aevatar.Foundation.Core.GAgentBase)
            .GetMethod("SetId", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(agent, [ActorId]);
        await agent.ActivateAsync();
        return agent;
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
            return Task.FromResult(new DispatchAdmission(
                true,
                actorId,
                DateTimeOffset.UtcNow,
                envelope.Id,
                envelope.Propagation?.CorrelationId ?? string.Empty));
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
