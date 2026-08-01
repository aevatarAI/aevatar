using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class ChatConversationGAgentAppendTests
{
    private const string ActorId = "chat-scope-a-conversation-a";
    private static readonly DateTimeOffset ConversationCreatedAt =
        DateTimeOffset.Parse("2026-07-15T00:00:00Z");

    [Fact]
    public async Task InitializeChatConversationCommand_ShouldCreateEmptyTranscript()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);

        await agent.HandleEventAsync(Envelope(CreateInitialize()));

        agent.State.ScopeId.Should().Be("scope-a");
        agent.State.ConversationId.Should().Be("conversation-a");
        agent.State.ServiceId.Should().Be("service-a");
        agent.State.ServiceKind.Should().Be("nyxid.chat");
        agent.State.Title.Should().Be("Initial title");
        agent.State.CreatedAtMs.Should().Be(ConversationCreatedAt.ToUnixTimeMilliseconds());
        agent.State.UpdatedAtMs.Should().Be(ConversationCreatedAt.ToUnixTimeMilliseconds());
        agent.State.NextTurnSequence.Should().Be(1);
        agent.State.Turns.Should().BeEmpty();
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(evt => evt.EventData.Is(ChatConversationInitializedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task InitializeChatConversationCommand_WhenExactlyRepeated_ShouldNotPersistAgain()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        var command = CreateInitialize();

        await agent.HandleEventAsync(Envelope(command));
        await agent.HandleEventAsync(Envelope(command.Clone()));

        agent.State.Turns.Should().BeEmpty();
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(evt => evt.EventData.Is(ChatConversationInitializedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task InitializeChatConversationCommand_WhenOperationOrPayloadChanges_ShouldFailClosed()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        await agent.HandleEventAsync(Envelope(CreateInitialize()));
        var conflicts = new[]
        {
            Changed(static command => command.OperationId = "initialize-2"),
            Changed(static command => command.ScopeId = "scope-b"),
            Changed(static command => command.ConversationId = "conversation-b"),
            Changed(static command => command.ServiceId = "service-b"),
            Changed(static command => command.ServiceKind = "workflow"),
            Changed(static command => command.CreatedAt = Timestamp.FromDateTimeOffset(ConversationCreatedAt.AddSeconds(1))),
            Changed(static command => command.InitialTitle = "Changed title"),
        };

        foreach (var conflict in conflicts)
        {
            var act = () => agent.HandleEventAsync(Envelope(conflict));

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*initialization conflicts*");
        }

        agent.State.ScopeId.Should().Be("scope-a");
        agent.State.ConversationId.Should().Be("conversation-a");
        agent.State.ServiceId.Should().Be("service-a");
        agent.State.ServiceKind.Should().Be("nyxid.chat");
        agent.State.Title.Should().Be("Initial title");
        agent.State.CreatedAtMs.Should().Be(ConversationCreatedAt.ToUnixTimeMilliseconds());
        agent.State.UpdatedAtMs.Should().Be(ConversationCreatedAt.ToUnixTimeMilliseconds());
        (await eventStore.GetEventsAsync(ActorId)).Should().ContainSingle();
    }

    [Fact]
    public async Task InitializeChatConversationCommand_AfterSameIdentityAppend_ShouldPreserveExistingTurn()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-1",
            "hello",
            "hi",
            ChatTurnTerminalStatus.Completed)));

        await agent.HandleEventAsync(Envelope(CreateInitialize()));

        agent.State.ScopeId.Should().Be("scope-a");
        agent.State.ConversationId.Should().Be("conversation-a");
        agent.State.ServiceId.Should().Be("service-a");
        agent.State.ServiceKind.Should().Be("nyxid.chat");
        agent.State.Title.Should().Be("hello");
        agent.State.CreatedAtMs.Should().Be(ConversationCreatedAt.ToUnixTimeMilliseconds());
        agent.State.UpdatedAtMs.Should().Be(
            DateTimeOffset.Parse("2026-07-16T00:00:00Z").ToUnixTimeMilliseconds());
        agent.State.Turns.Should().ContainSingle().Which.TurnId.Should().Be("turn-1");
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(evt => evt.EventData.Is(ChatTurnAppendedEvent.Descriptor)).Should().Be(1);
        persisted.Count(evt => evt.EventData.Is(ChatConversationInitializedEvent.Descriptor)).Should().Be(1);
    }

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
        agent.State.NextTurnSequence.Should().Be(3);
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
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        var command = CreateAppend("turn-1", "hello", "hi", ChatTurnTerminalStatus.Completed);

        await agent.HandleEventAsync(Envelope(command));
        await agent.HandleEventAsync(Envelope(command.Clone()));
        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-2",
            "continue",
            "continued",
            ChatTurnTerminalStatus.Completed)));

        agent.State.Turns.Should().HaveCount(2);
        agent.State.Turns[0].Sequence.Should().Be(1);
        agent.State.Turns[1].Sequence.Should().Be(2);
        agent.State.LastRejectedAppend.Should().BeNull();
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(evt => evt.EventData.Is(ChatTurnAppendedEvent.Descriptor)).Should().Be(2);
        persisted.Should().NotContain(evt => evt.EventData.Is(ChatTurnAppendRejectedEvent.Descriptor));
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

    [Theory]
    [InlineData(ChatTurnTerminalStatus.Completed)]
    [InlineData(ChatTurnTerminalStatus.Failed)]
    public async Task AppendChatTurnCommand_ShouldReconcileOutcomeUncertainExactlyOnce(
        ChatTurnTerminalStatus reconciledStatus)
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        var uncertain = CreateAppend(
            "turn-uncertain",
            "perform side effect",
            "The outcome could not be confirmed.",
            ChatTurnTerminalStatus.OutcomeUncertain);
        uncertain.Turn.SanitizedError = "SESSION_OUTCOME_UNCERTAIN";
        var reconciled = CreateAppend(
            "turn-uncertain",
            "perform side effect",
            reconciledStatus == ChatTurnTerminalStatus.Completed ? "confirmed result" : string.Empty,
            reconciledStatus);
        reconciled.Turn.SanitizedError = reconciledStatus == ChatTurnTerminalStatus.Failed
            ? "CONFIRMED_FAILURE"
            : string.Empty;

        await agent.HandleEventAsync(Envelope(uncertain));
        await agent.HandleEventAsync(Envelope(reconciled));
        await agent.HandleEventAsync(Envelope(reconciled.Clone()));

        var turn = agent.State.Turns.Should().ContainSingle().Which;
        turn.TurnId.Should().Be("turn-uncertain");
        turn.Sequence.Should().Be(1);
        turn.TerminalStatus.Should().Be(reconciledStatus);
        turn.AssistantText.Should().Be(reconciled.Turn.AssistantText);
        turn.SanitizedError.Should().Be(reconciled.Turn.SanitizedError);
        agent.State.LastRejectedAppend.Should().BeNull();
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(stateEvent => stateEvent.EventData.Is(ChatTurnAppendedEvent.Descriptor))
            .Should().Be(1);
        persisted.Count(stateEvent => stateEvent.EventData.Is(ChatTurnTerminalReconciledEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldRejectReconciliationAfterAbsorbingTerminal()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-terminal",
            "perform work",
            "completed",
            ChatTurnTerminalStatus.Completed)));

        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-terminal",
            "perform work",
            string.Empty,
            ChatTurnTerminalStatus.Failed)));

        agent.State.Turns.Should().ContainSingle().Which.TerminalStatus
            .Should().Be(ChatTurnTerminalStatus.Completed);
        agent.State.LastRejectedAppend!.Reason.Should().Be(ChatTurnAppendRejectionReason.Conflict);
        (await eventStore.GetEventsAsync(ActorId)).Should().NotContain(stateEvent =>
            stateEvent.EventData.Is(ChatTurnTerminalReconciledEvent.Descriptor));
    }

    [Fact]
    public async Task AppendChatTurnCommand_ShouldRemainAppendableBeyondFormerTurnLimit()
    {
        var agent = await CreateAgentAsync();
        for (var i = 1; i <= 250; i++)
        {
            await agent.HandleEventAsync(Envelope(CreateAppend($"turn-{i}", $"user-{i}", $"assistant-{i}", ChatTurnTerminalStatus.Completed)));
        }

        await agent.HandleEventAsync(Envelope(CreateAppend("turn-251", "continued", "continued", ChatTurnTerminalStatus.Completed)));

        agent.State.Turns.Should().HaveCount(251);
        agent.State.Turns[0].TurnId.Should().Be("turn-1");
        agent.State.Turns[^1].TurnId.Should().Be("turn-251");
        agent.State.Turns[^1].Sequence.Should().Be(251);
        agent.State.NextTurnSequence.Should().Be(252);
        agent.State.LastRejectedAppend.Should().BeNull();
    }

    [Fact]
    public async Task AppendChatTurnCommand_AfterReactivationAndDuplicate_ShouldKeepSequenceMonotonic()
    {
        var eventStore = new RecordingEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch, eventStore);
        for (var i = 1; i <= 251; i++)
        {
            await agent.HandleEventAsync(Envelope(CreateAppend($"turn-{i}", $"user-{i}", $"assistant-{i}", ChatTurnTerminalStatus.Completed)));
        }

        var repeated = CreateAppend("turn-251", "user-251", "assistant-251", ChatTurnTerminalStatus.Completed);
        repeated.DeliveryActorId = "delivery-actor";
        var reactivated = await CreateAgentAsync(dispatch, eventStore);

        await reactivated.HandleEventAsync(Envelope(repeated));
        await reactivated.HandleEventAsync(Envelope(CreateAppend(
            "turn-252",
            "user-252",
            "assistant-252",
            ChatTurnTerminalStatus.Completed)));

        reactivated.State.Turns.Should().HaveCount(252);
        reactivated.State.Turns[^1].Sequence.Should().Be(252);
        reactivated.State.NextTurnSequence.Should().Be(253);
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(evt => evt.EventData.Is(ChatTurnAppendedEvent.Descriptor)).Should().Be(252);
        dispatch.Calls.Should().ContainSingle();
        var call = dispatch.Calls.Single();
        call.ActorId.Should().Be("delivery-actor");
        var result = call.Envelope.Payload.Unpack<ChatTurnHistoryDeliveryAppendResultObserved>();
        result.DeliveryActorId.Should().Be("delivery-actor");
        result.ConversationId.Should().Be("conversation-a");
        result.TurnId.Should().Be("turn-251");
        result.Accepted.Should().BeTrue();
        result.RejectionReason.Should().Be(ChatTurnAppendRejectionReason.Unspecified);
    }

    [Fact]
    public async Task AppendChatTurnCommand_WhenReplayingLegacyTurn_ShouldRecoverSequenceWatermark()
    {
        var eventStore = new RecordingEventStore();
        var legacyTurn = CreateAppend(
            "turn-250",
            "legacy-user",
            "legacy-assistant",
            ChatTurnTerminalStatus.Completed).Turn;
        legacyTurn.Sequence = 250;
        await eventStore.AppendAsync(
            ActorId,
            [
                new StateEvent
                {
                    EventId = "legacy-turn-event",
                    EventType = ChatTurnAppendedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new ChatTurnAppendedEvent
                    {
                        ScopeId = "scope-a",
                        ConversationId = "conversation-a",
                        Turn = legacyTurn,
                    }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-16T00:00:00Z")),
                },
            ],
            expectedVersion: 0);
        var agent = await CreateAgentAsync(eventStore: eventStore);

        agent.State.NextTurnSequence.Should().Be(251);
        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-251",
            "new-user",
            "new-assistant",
            ChatTurnTerminalStatus.Completed)));

        agent.State.Turns.Should().HaveCount(2);
        agent.State.Turns[^1].Sequence.Should().Be(251);
        agent.State.NextTurnSequence.Should().Be(252);
    }

    [Fact]
    public async Task DeleteConversationCommand_AfterReactivation_ShouldReplayTheCommittedDeletionTimeExactly()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        await agent.HandleEventAsync(Envelope(CreateInitialize()));

        await agent.HandleEventAsync(Envelope(CreateDelete()));

        var persisted = await eventStore.GetEventsAsync(ActorId);
        var deletedEvent = persisted
            .Should().ContainSingle(evt => evt.EventData.Is(ConversationDeletedEvent.Descriptor))
            .Which.EventData.Unpack<ConversationDeletedEvent>();
        deletedEvent.DeletedAt.Should().NotBeNull();
        agent.State.Deleted.Should().BeTrue();
        agent.State.UpdatedAtMs.Should().Be(deletedEvent.DeletedAt.ToDateTimeOffset().ToUnixTimeMilliseconds());
        var committedStateRoot = agent.State.ToByteString();

        var reactivated = await CreateAgentAsync(eventStore: eventStore);

        reactivated.State.ToByteString().Should().Equal(committedStateRoot);
        reactivated.State.UpdatedAtMs.Should().Be(deletedEvent.DeletedAt.ToDateTimeOffset().ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task LegacyConversationDeletedEvent_WithoutDeletionTime_ShouldReplayWithoutReadingTheClock()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        await agent.HandleEventAsync(Envelope(CreateInitialize()));
        await eventStore.AppendAsync(
            ActorId,
            [
                new StateEvent
                {
                    EventId = "legacy-delete-event",
                    EventType = ConversationDeletedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new ConversationDeletedEvent
                    {
                        ScopeId = "scope-a",
                        ConversationId = "conversation-a",
                    }),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-17T00:00:00Z")),
                },
            ],
            expectedVersion: 1);

        var firstReplay = await CreateAgentAsync(eventStore: eventStore);
        var firstStateRoot = firstReplay.State.ToByteString();
        var secondReplay = await CreateAgentAsync(eventStore: eventStore);

        firstReplay.State.Deleted.Should().BeTrue();
        firstReplay.State.UpdatedAtMs.Should().Be(ConversationCreatedAt.ToUnixTimeMilliseconds());
        secondReplay.State.ToByteString().Should().Equal(firstStateRoot);
    }

    [Fact]
    public async Task DeleteConversationCommand_WhenExactlyRepeated_ShouldNotPersistOrChangeStateAgain()
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        await agent.HandleEventAsync(Envelope(CreateInitialize()));
        var command = CreateDelete();

        await agent.HandleEventAsync(Envelope(command));
        var stateAfterDelete = agent.State.ToByteString();
        await agent.HandleEventAsync(Envelope(command.Clone()));

        agent.State.ToByteString().Should().Equal(stateAfterDelete);
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(evt => evt.EventData.Is(ConversationDeletedEvent.Descriptor)).Should().Be(1);
    }

    [Theory]
    [InlineData("scope-b", "conversation-a")]
    [InlineData("scope-a", "conversation-b")]
    public async Task DeleteConversationCommand_WhenBoundIdentityDiffers_ShouldFailClosed(
        string scopeId,
        string conversationId)
    {
        var eventStore = new RecordingEventStore();
        var agent = await CreateAgentAsync(eventStore: eventStore);
        await agent.HandleEventAsync(Envelope(CreateInitialize()));
        var command = new DeleteConversationCommand
        {
            ScopeId = scopeId,
            ConversationId = conversationId,
        };

        var act = () => agent.HandleEventAsync(Envelope(command));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*deletion conflicts*");
        agent.State.Deleted.Should().BeFalse();
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Should().NotContain(evt => evt.EventData.Is(ConversationDeletedEvent.Descriptor));
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

    private static InitializeChatConversationCommand CreateInitialize() =>
        new()
        {
            OperationId = "initialize-1",
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            ServiceId = "service-a",
            ServiceKind = "nyxid.chat",
            CreatedAt = Timestamp.FromDateTimeOffset(ConversationCreatedAt),
            InitialTitle = "Initial title",
        };

    private static DeleteConversationCommand CreateDelete() =>
        new()
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
        };

    private static InitializeChatConversationCommand Changed(
        Action<InitializeChatConversationCommand> change)
    {
        var command = CreateInitialize();
        change(command);
        return command;
    }

    private static EventEnvelope Envelope(Google.Protobuf.IMessage payload) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", ActorId),
        };

    private static async Task<ChatConversationGAgent> CreateAgentAsync(
        IActorDispatchPort? dispatchPort = null,
        RecordingEventStore? eventStore = null)
    {
        eventStore ??= new RecordingEventStore();
        var services = new ServiceCollection()
            .AddSingleton<IEventStore>(eventStore)
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
