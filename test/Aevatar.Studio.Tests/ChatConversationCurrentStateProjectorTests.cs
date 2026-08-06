using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Runtime.Abstractions;
using Aevatar.CQRS.Projection.Stores.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.GAgents.ChatHistory;
using Aevatar.Studio.Projection.Orchestration;
using Aevatar.Studio.Projection.Projectors;
using Aevatar.Studio.Projection.ReadModels;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Studio.Tests;

public sealed class ChatConversationCurrentStateProjectorTests
{
    private const string RootActorId = "chat-history-conversation-scope-a-conversation-a";

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeInitializedConversationWithoutTurns()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-28T09:00:00Z")));
        var state = new ChatConversationState
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            Title = "Initial title",
            ServiceId = "service-a",
            ServiceKind = "nyxid.chat",
            CreatedAtMs = 1785200523000,
            UpdatedAtMs = 1785200523000,
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatConversationInitializedEvent
                {
                    OperationId = "initialize-1",
                    ScopeId = "scope-a",
                    ConversationId = "conversation-a",
                },
                state,
                version: 1,
                eventId: "evt-chat-initialized",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-28T01:02:03Z")));

        var document = dispatcher.Upserts.Should().ContainSingle().Subject;
        document.ScopeId.Should().Be("scope-a");
        document.ConversationId.Should().Be("conversation-a");
        document.ServiceKind.Should().Be("nyxid.chat");
        document.StateVersion.Should().Be(1);
        document.MessageCount.Should().Be(0);
        document.Turns.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeBlockedTurnStatus()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-20T09:00:00Z")));
        var state = new ChatConversationState
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            Turns =
            {
                new ChatTurn
                {
                    TurnId = "turn-blocked",
                    Sequence = 1,
                    UserText = "read private resource",
                    TerminalStatus = ChatTurnTerminalStatus.Blocked,
                    SanitizedError = "Connect api-github to continue.",
                },
            },
        };

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatTurnAppendedEvent { ScopeId = "scope-a", ConversationId = "conversation-a" },
                state,
                version: 1,
                eventId: "evt-chat-blocked",
                stateEventTimestamp: DateTimeOffset.Parse("2026-07-20T08:30:00Z")));

        dispatcher.Upserts.Should().ContainSingle().Which.Turns.Should()
            .ContainSingle(turn =>
                turn.TurnId == "turn-blocked" &&
                turn.TerminalStatus == "blocked" &&
                turn.SanitizedError == "Connect api-github to continue.");
    }

    [Fact]
    public async Task ProjectAsync_ShouldMaterializeConversationStateAndTerminalTurnNames()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-07-16T09:00:00Z")));
        var stateEventTimestamp = DateTimeOffset.Parse("2026-07-16T08:30:00Z");
        var terminalTime = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-16T08:20:00Z"));
        var state = new ChatConversationState
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            Title = "Support conversation",
            ServiceId = "svc-alpha",
            ServiceKind = "workflow",
            CreatedAtMs = 1784170000000,
            UpdatedAtMs = 1784170300000,
            Deleted = true,
        };
        state.Turns.AddRange(new[]
        {
            new ChatTurn
            {
                TurnId = "turn-1",
                Sequence = 1,
                UserText = "hello",
                AssistantText = "hi",
                TerminalStatus = ChatTurnTerminalStatus.Completed,
                TerminalTime = terminalTime,
                LlmRoute = "route-a",
                LlmModel = "model-a",
            },
            new ChatTurn
            {
                TurnId = "turn-2",
                Sequence = 2,
                UserText = "fail",
                AssistantText = "failed",
                TerminalStatus = ChatTurnTerminalStatus.Failed,
                SanitizedError = "safe error",
                TerminalTime = terminalTime,
                LlmRoute = "route-b",
                LlmModel = "model-b",
            },
            new ChatTurn
            {
                TurnId = "turn-3",
                Sequence = 3,
                UserText = "stop",
                AssistantText = "stopped",
                TerminalStatus = ChatTurnTerminalStatus.Stopped,
                TerminalTime = terminalTime,
                LlmRoute = "route-c",
                LlmModel = "model-c",
            },
            new ChatTurn
            {
                TurnId = "turn-4",
                Sequence = 4,
                UserText = "side effect",
                AssistantText = "outcome unknown",
                TerminalStatus = ChatTurnTerminalStatus.OutcomeUncertain,
                SanitizedError = "SESSION_OUTCOME_UNCERTAIN",
                LlmRoute = "route-d",
                LlmModel = "model-d",
            },
            new ChatTurn
            {
                TurnId = "turn-5",
                Sequence = 5,
                UserText = "pending",
                AssistantText = "no terminal name",
                TerminalStatus = ChatTurnTerminalStatus.Unspecified,
                LlmRoute = "route-final",
                LlmModel = "model-final",
            },
        });

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatTurnAppendedEvent { ScopeId = "scope-a", ConversationId = "conversation-a" },
                state,
                version: 7,
                eventId: "evt-chat-7",
                stateEventTimestamp: stateEventTimestamp));

        var written = dispatcher.Upserts.Should().ContainSingle().Subject;
        written.Id.Should().Be(RootActorId);
        written.ActorId.Should().Be(RootActorId);
        written.StateVersion.Should().Be(7);
        written.LastEventId.Should().Be("evt-chat-7");
        written.UpdatedAt.ToDateTimeOffset().Should().Be(stateEventTimestamp);
        written.ScopeId.Should().Be("scope-a");
        written.ConversationId.Should().Be("conversation-a");
        written.Title.Should().Be("Support conversation");
        written.ServiceId.Should().Be("svc-alpha");
        written.ServiceKind.Should().Be("workflow");
        written.CreatedAtMs.Should().Be(1784170000000);
        written.UpdatedAtMs.Should().Be(1784170300000);
        written.MessageCount.Should().Be(5);
        written.LlmRoute.Should().Be("route-final");
        written.LlmModel.Should().Be("model-final");
        written.Deleted.Should().BeTrue();

        written.Turns.Should().HaveCount(5);
        written.Turns.Select(turn => turn.TerminalStatus)
            .Should().Equal("complete", "error", "stopped", "outcome_uncertain", string.Empty);
        written.Turns[0].TurnId.Should().Be("turn-1");
        written.Turns[0].Sequence.Should().Be(1);
        written.Turns[0].UserText.Should().Be("hello");
        written.Turns[0].AssistantText.Should().Be("hi");
        written.Turns[0].TerminalStatus.Should().Be("complete");
        written.Turns[0].TerminalTimeMs.Should().Be(terminalTime.ToDateTimeOffset().ToUnixTimeMilliseconds());
        written.Turns[0].LlmRoute.Should().Be("route-a");
        written.Turns[0].LlmModel.Should().Be("model-a");
        written.Turns[1].SanitizedError.Should().Be("safe error");
        written.Turns[3].SanitizedError.Should().Be("SESSION_OUTCOME_UNCERTAIN");
        written.Turns[4].TerminalTimeMs.Should().Be(0);
    }

    [Fact]
    public async Task ProjectAsync_ShouldKeepEntireTranscriptBeyondPromptWindow()
    {
        var dispatcher = new RecordingWriteDispatcher();
        var projector = new ChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-01T09:00:00Z")));
        var state = new ChatConversationState
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            NextTurnSequence = 252,
        };
        for (var sequence = 1; sequence <= 251; sequence++)
        {
            state.Turns.Add(new ChatTurn
            {
                TurnId = $"turn-{sequence}",
                Sequence = sequence,
                UserText = $"user-{sequence}",
                AssistantText = $"assistant-{sequence}",
                TerminalStatus = ChatTurnTerminalStatus.Completed,
            });
        }

        await projector.ProjectAsync(
            NewContext(),
            WrapCommitted(
                new ChatTurnAppendedEvent { ScopeId = "scope-a", ConversationId = "conversation-a" },
                state,
                version: 251,
                eventId: "evt-chat-251",
                stateEventTimestamp: DateTimeOffset.Parse("2026-08-01T08:30:00Z")));

        var written = dispatcher.Upserts.Should().ContainSingle().Subject;
        written.MessageCount.Should().Be(251);
        written.Turns.Should().HaveCount(251);
        written.Turns[0].TurnId.Should().Be("turn-1");
        written.Turns[^1].TurnId.Should().Be("turn-251");
        written.Turns[^1].Sequence.Should().Be(251);
    }

    [Fact]
    public async Task ProjectAsync_WhenDeletionIsRedeliveredExactly_ShouldKeepContentStableAndReportDuplicate()
    {
        var dispatcher = new IdempotencyRecordingWriteDispatcher();
        var projector = new ChatConversationCurrentStateProjector(
            dispatcher,
            new FixedProjectionClock(DateTimeOffset.Parse("2026-08-02T09:00:00Z")));
        var deletedAt = DateTimeOffset.Parse("2026-08-02T08:30:00Z");
        var deletedEvent = new ConversationDeletedEvent
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            DeletedAt = Timestamp.FromDateTimeOffset(deletedAt),
        };
        var state = new ChatConversationState
        {
            ScopeId = "scope-a",
            ConversationId = "conversation-a",
            Deleted = true,
            UpdatedAtMs = deletedAt.ToUnixTimeMilliseconds(),
            NextTurnSequence = 1,
        };
        var envelope = WrapCommitted(
            deletedEvent,
            state,
            version: 2,
            eventId: "evt-chat-deleted",
            stateEventTimestamp: deletedAt);

        await projector.ProjectAsync(NewContext(), envelope);
        await projector.ProjectAsync(NewContext(), envelope.Clone());

        dispatcher.Results.Select(static result => result.Disposition).Should().Equal(
            ProjectionWriteDisposition.Applied,
            ProjectionWriteDisposition.Duplicate);
        dispatcher.Inputs.Should().HaveCount(2);
        dispatcher.Inputs[1].ToByteString().Should().Equal(dispatcher.Inputs[0].ToByteString());
        dispatcher.Inputs[1].UpdatedAtMs.Should().Be(deletedAt.ToUnixTimeMilliseconds());
        dispatcher.Inputs[1].Deleted.Should().BeTrue();
    }

    private static StudioMaterializationContext NewContext() => new()
    {
        RootActorId = RootActorId,
        ProjectionKind = "studio-current-state",
    };

    private static EventEnvelope WrapCommitted(
        IMessage payload,
        ChatConversationState state,
        long version,
        string eventId,
        DateTimeOffset stateEventTimestamp)
    {
        return new EventEnvelope
        {
            Id = eventId,
            Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.Parse("2026-07-16T08:00:00Z")),
            Route = EnvelopeRouteSemantics.CreateObserverPublication(RootActorId),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = eventId,
                    Version = version,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTimeOffset(stateEventTimestamp),
                },
                StateRoot = Any.Pack(state),
            }),
        };
    }

    private sealed class RecordingWriteDispatcher
        : IProjectionWriteDispatcher<ChatConversationCurrentStateDocument>
    {
        public List<ChatConversationCurrentStateDocument> Upserts { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Upserts.Add(readModel);
            return Task.FromResult(ProjectionWriteResult.Applied());
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class IdempotencyRecordingWriteDispatcher
        : IProjectionWriteDispatcher<ChatConversationCurrentStateDocument>
    {
        private ChatConversationCurrentStateDocument? _current;

        public List<ChatConversationCurrentStateDocument> Inputs { get; } = [];
        public List<ProjectionWriteResult> Results { get; } = [];

        public Task<ProjectionWriteResult> UpsertAsync(
            ChatConversationCurrentStateDocument readModel,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var input = readModel.Clone();
            var result = ProjectionWriteResultEvaluator.Evaluate(_current, input);
            Inputs.Add(input);
            Results.Add(result);
            if (result.IsApplied)
                _current = input.Clone();
            return Task.FromResult(result);
        }

        public Task<ProjectionWriteResult> DeleteAsync(string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FixedProjectionClock(DateTimeOffset utcNow) : IProjectionClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
