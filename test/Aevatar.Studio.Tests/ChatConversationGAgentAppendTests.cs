using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Abstractions.Persistence;
using Aevatar.Foundation.Abstractions.Runtime.Callbacks;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Application.Studio.Abstractions;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Tests;

public sealed class ChatConversationGAgentAppendTests
{
    private static readonly string ActorId =
        ChatHistoryActorIds.Conversation("scope-a", "conversation-a");

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
    public async Task ConversationDeletion_ShouldDispatchCommittedCompletionAfterPersisting()
    {
        var eventStore = new RecordingEventStore();
        var deletionWasCommittedAtDispatch = false;
        var dispatch = new RecordingActorDispatchPort(async (_, _) =>
        {
            var events = await eventStore.GetEventsAsync(ActorId);
            deletionWasCommittedAtDispatch = events.Any(entry =>
                entry.EventData.Is(ConversationDeletedEvent.Descriptor));
        });
        var agent = await CreateAgentAsync(dispatch, eventStore);
        await agent.HandleEventAsync(Envelope(CreateAppend(
            "turn-1",
            "hello",
            "hi",
            ChatTurnTerminalStatus.Completed)));
        var deletion = new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "history-delete-operation-alpha",
            CompletionActorId = "nyxid-conversation-alpha",
        };

        await agent.HandleEventAsync(Envelope(deletion));

        deletionWasCommittedAtDispatch.Should().BeTrue();
        dispatch.Calls.Should().ContainSingle();
        dispatch.Calls.Single().ActorId.Should().Be("nyxid-conversation-alpha");
        var firstCompletion = dispatch.Calls.Single().Envelope.Payload
            .Unpack<ChatHistoryConversationDeletionCommitted>();
        firstCompletion.OperationId.Should().Be("history-delete-operation-alpha");
        firstCompletion.ScopeId.Should().Be("scope-a");
        firstCompletion.ConversationId.Should().Be("conversation-a");
        firstCompletion.CommittedAt.Should().NotBeNull();
        dispatch.Calls.Single().Envelope.Route.PublisherActorId.Should().Be(ActorId);

        await agent.HandleEventAsync(Envelope(deletion.Clone()));

        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls[1].ActorId.Should().Be("nyxid-conversation-alpha");
        dispatch.Calls[1].Envelope.Payload.Unpack<ChatHistoryConversationDeletionCommitted>()
            .Should().BeEquivalentTo(firstCompletion);
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(entry => entry.EventData.Is(ConversationDeletedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task ConversationDeletion_EmptyCanonicalActor_ShouldCommitIdempotentTombstoneBeforeCompletion()
    {
        var eventStore = new RecordingEventStore();
        var deletionWasCommittedAtDispatch = false;
        var dispatch = new RecordingActorDispatchPort(async (_, _) =>
        {
            var events = await eventStore.GetEventsAsync(ActorId);
            deletionWasCommittedAtDispatch = events.Any(entry =>
                entry.EventData.Is(ConversationDeletedEvent.Descriptor));
        });
        var agent = await CreateAgentAsync(dispatch, eventStore);
        var deletion = new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "history-delete-operation-empty",
            CompletionActorId = "nyxid-conversation-alpha",
        };

        await agent.HandleEventAsync(Envelope(deletion));

        deletionWasCommittedAtDispatch.Should().BeTrue();
        agent.State.Deleted.Should().BeTrue();
        agent.State.ScopeId.Should().Be("scope-a");
        agent.State.ConversationId.Should().Be("conversation-a");
        var firstCompletion = dispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<ChatHistoryConversationDeletionCommitted>();
        firstCompletion.OperationId.Should().Be(deletion.OperationId);

        await agent.HandleEventAsync(Envelope(deletion.Clone()));

        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls[1].Envelope.Payload.Unpack<ChatHistoryConversationDeletionCommitted>()
            .Should().BeEquivalentTo(firstCompletion);
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(entry => entry.EventData.Is(ConversationDeletedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task ConversationDeletion_PublicTombstoneThenLifecycleOperation_ShouldAcknowledgeNewOperation()
    {
        var eventStore = new RecordingEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch, eventStore);
        await agent.HandleEventAsync(Envelope(new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "public-delete-operation",
        }));
        var lifecycleDeletion = new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "lifecycle-delete-operation",
            CompletionActorId = "nyxid-conversation-alpha",
        };

        await agent.HandleEventAsync(Envelope(lifecycleDeletion));
        await agent.HandleEventAsync(Envelope(lifecycleDeletion.Clone()));
        var changedCallback = lifecycleDeletion.Clone();
        changedCallback.CompletionActorId = "nyxid-conversation-other";
        await agent.HandleEventAsync(Envelope(changedCallback));

        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls.Should().OnlyContain(call => call.ActorId == "nyxid-conversation-alpha");
        dispatch.Calls.Select(call => call.Envelope.Payload.Unpack<ChatHistoryConversationDeletionCommitted>())
            .Should().OnlyContain(completion => completion.OperationId == lifecycleDeletion.OperationId);
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(entry => entry.EventData.Is(ConversationDeletedEvent.Descriptor))
            .Should().Be(1, "the original tombstone remains the authoritative deletion fact");
    }

    [Fact]
    public async Task ConversationDeletion_LifecycleTombstoneThenPublicOperation_ShouldPreserveLifecycleAcknowledgement()
    {
        var eventStore = new RecordingEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch, eventStore);
        var lifecycleDeletion = new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "lifecycle-delete-operation",
            CompletionActorId = "nyxid-conversation-alpha",
        };
        await agent.HandleEventAsync(Envelope(lifecycleDeletion));

        await agent.HandleEventAsync(Envelope(new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "public-delete-operation",
        }));
        await agent.HandleEventAsync(Envelope(lifecycleDeletion.Clone()));

        dispatch.Calls.Should().HaveCount(2);
        dispatch.Calls.Select(call => call.Envelope.Payload.Unpack<ChatHistoryConversationDeletionCommitted>())
            .Should().OnlyContain(completion =>
                completion.OperationId == lifecycleDeletion.OperationId &&
                completion.CompletionActorId == lifecycleDeletion.CompletionActorId);
        var persisted = await eventStore.GetEventsAsync(ActorId);
        persisted.Count(entry => entry.EventData.Is(ConversationDeletedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task ConversationDeletion_ReplayedAcknowledgementWithForeignOwnerActor_ShouldNotDispatch()
    {
        const string operationId = "lifecycle-delete-operation";
        var eventStore = new RecordingEventStore();
        await eventStore.AppendAsync(
            ActorId,
            [
                new StateEvent
                {
                    EventId = "foreign-owner-acknowledgement",
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    EventType = ConversationDeletionAcknowledgedEvent.Descriptor.FullName,
                    EventData = Any.Pack(new ConversationDeletionAcknowledgedEvent
                    {
                        OperationId = operationId,
                        ScopeId = "scope-a",
                        ConversationId = "conversation-a",
                        CompletionActorId = "nyxid-conversation-alpha",
                        OwnerActorId = "chat-history-conversation-foreign",
                        OwnerKind = ConversationDeletionOwnerKind.Canonical,
                        Outcome = ConversationDeletionAcknowledgementOutcome.CommittedDeleted,
                        CommittedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    }),
                    AgentId = ActorId,
                },
            ],
            expectedVersion: 0);
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch, eventStore);

        await agent.HandleEventAsync(Envelope(new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = operationId,
            CompletionActorId = "nyxid-conversation-alpha",
        }));

        dispatch.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ConversationDeletion_PristineLegacyOwner_ShouldAcknowledgeAbsenceWithoutTombstone()
    {
        var legacyActorId = ChatHistoryActorIds.LegacyConversation("scope-a", "conversation-a");
        var eventStore = new RecordingEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch, eventStore, legacyActorId);

        await agent.HandleEventAsync(Envelope(new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "lifecycle-delete-operation",
            CompletionActorId = "nyxid-conversation-alpha",
        }, legacyActorId));

        agent.State.Deleted.Should().BeFalse();
        dispatch.Calls.Should().ContainSingle().Which.ActorId.Should().Be("nyxid-conversation-alpha");
        var persisted = await eventStore.GetEventsAsync(legacyActorId);
        persisted.Should().NotContain(entry => entry.EventData.Is(ConversationDeletedEvent.Descriptor));
    }

    [Fact]
    public async Task ConversationDeletion_MatchingLiveLegacyOwner_ShouldCommitTombstone()
    {
        var legacyActorId = ChatHistoryActorIds.LegacyConversation("scope-a", "conversation-a");
        var eventStore = new RecordingEventStore();
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch, eventStore, legacyActorId);
        await agent.HandleEventAsync(Envelope(
            CreateAppend("turn-legacy", "hello", "hi", ChatTurnTerminalStatus.Completed),
            legacyActorId));

        await agent.HandleEventAsync(Envelope(new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            OperationId = "lifecycle-delete-operation",
            CompletionActorId = "nyxid-conversation-alpha",
        }, legacyActorId));

        agent.State.Deleted.Should().BeTrue();
        var completion = dispatch.Calls.Should().ContainSingle().Which.Envelope.Payload
            .Unpack<ChatHistoryConversationDeletionCommitted>();
        completion.OwnerActorId.Should().Be(legacyActorId);
        completion.OwnerKind.Should().Be(ChatHistoryConversationOwnerKind.Legacy);
        completion.Outcome.Should().Be(ChatHistoryConversationDeletionOutcome.CommittedDeleted);
        var persisted = await eventStore.GetEventsAsync(legacyActorId);
        persisted.Count(entry => entry.EventData.Is(ConversationDeletedEvent.Descriptor))
            .Should().Be(1);
    }

    [Fact]
    public async Task ConversationDeletion_CollidingLegacyOwner_ShouldAcknowledgeAbsenceWithoutMutatingTuple()
    {
        var legacyActorId = ChatHistoryActorIds.LegacyConversation("tenant", "admin-c1");
        legacyActorId.Should().Be(ChatHistoryActorIds.LegacyConversation("tenant-admin", "c1"));
        var dispatch = new RecordingActorDispatchPort();
        var agent = await CreateAgentAsync(dispatch, actorId: legacyActorId);
        var ownedAppend = CreateAppend("turn-owned", "owned", "secret", ChatTurnTerminalStatus.Completed);
        ownedAppend.ScopeId = "tenant";
        ownedAppend.ConversationId = "admin-c1";
        await agent.HandleEventAsync(Envelope(ownedAppend, legacyActorId));

        await agent.HandleEventAsync(Envelope(new ConversationDeletedEvent
        {
            ScopeId = "tenant-admin",
            ConversationId = "c1",
            OperationId = "lifecycle-delete-operation",
            CompletionActorId = "nyxid-conversation-alpha",
        }, legacyActorId));

        agent.State.Deleted.Should().BeFalse();
        agent.State.ScopeId.Should().Be("tenant");
        agent.State.ConversationId.Should().Be("admin-c1");
        agent.State.Turns.Should().ContainSingle().Which.TurnId.Should().Be("turn-owned");
        dispatch.Calls.Should().ContainSingle().Which.ActorId.Should().Be("nyxid-conversation-alpha");
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

    private static EventEnvelope Envelope(Google.Protobuf.IMessage payload, string? actorId = null) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Payload = Any.Pack(payload),
            Route = EnvelopeRouteSemantics.CreateDirect("test", actorId ?? ActorId),
        };

    private static async Task<ChatConversationGAgent> CreateAgentAsync(
        IActorDispatchPort? dispatchPort = null,
        RecordingEventStore? eventStore = null,
        string? actorId = null)
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
            .Invoke(agent, [actorId ?? ActorId]);
        await agent.ActivateAsync();
        return agent;
    }

    private sealed class RecordingActorDispatchPort(
        Func<string, EventEnvelope, Task>? onDispatch = null) : IActorDispatchPort
    {
        public List<(string ActorId, EventEnvelope Envelope)> Calls { get; } = [];

        public async Task<DispatchAdmission> DispatchAsync(
            string actorId,
            EventEnvelope envelope,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((actorId, envelope));
            if (onDispatch is not null)
                await onDispatch(actorId, envelope);
            return new DispatchAdmission(
                true,
                actorId,
                DateTimeOffset.UtcNow,
                envelope.Id,
                envelope.Propagation?.CorrelationId ?? string.Empty);
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
