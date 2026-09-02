using Aevatar.AGUI.Contracts;
using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgents.NyxidChat;
using FluentAssertions;
using Google.Protobuf;
using Any = Google.Protobuf.WellKnownTypes.Any;
using Timestamp = Google.Protobuf.WellKnownTypes.Timestamp;

namespace Aevatar.AI.Tests;

public sealed class NyxIdChatCommittedProgressProjectionTests
{
    [Fact]
    public async Task ProjectAsync_ShouldPublishSequencedAguiFrames_FromCommittedProgressEnvelopes()
    {
        var sessionHub = new RecordingSessionEventHub();
        var projector = new NyxIdChatSessionEventProjector(sessionHub);
        var context = new NyxIdChatSessionProjectionContext
        {
            RootActorId = "actor-1",
            SessionId = "session-1",
            ProjectionKind = NyxIdChatProjectionKinds.ChatSession,
        };
        var progress = new RoleChatSessionProgressedEvent[]
        {
            new()
            {
                SessionId = context.SessionId,
                Sequence = 1,
                TextStarted = new RoleChatTextStartedProgress { AgentId = context.RootActorId },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 2,
                TextDelta = new RoleChatTextDeltaProgress { Delta = "done" },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 3,
                Usage = new RoleChatUsageProgress
                {
                    Usage = new TokenUsagePayload
                    {
                        PromptTokens = 2,
                        CompletionTokens = 4,
                        TotalTokens = 6,
                    },
                    Model = "nyxid-model",
                },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 4,
                TextEnded = new RoleChatTextEndedProgress { MessageId = context.SessionId },
            },
            new()
            {
                SessionId = context.SessionId,
                Sequence = 5,
                Terminal = new RoleChatTerminalProgress
                {
                    Outcome = RoleChatSessionOutcome.Completed,
                    FinalContent = "done",
                },
            },
        };

        foreach (var item in progress)
        {
            await projector.ProjectAsync(
                context,
                CommittedEnvelope(context.RootActorId, item),
                CancellationToken.None);
        }

        sessionHub.Published.Should().HaveCount(5);
        sessionHub.Published[0].Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.TextMessageStart);
        sessionHub.Published[1].Event.TextMessageContent.Delta.Should().Be("done");
        sessionHub.Published[2].Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.Usage);
        sessionHub.Published[2].Event.Usage.Available.Should().BeTrue();
        sessionHub.Published[2].Event.Usage.TotalTokens.Should().Be(6);
        sessionHub.Published[3].Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.TextMessageEnd);
        sessionHub.Published[4].Event.EventCase.Should().Be(AGUIEvent.EventOneofCase.RunFinished);
        sessionHub.Published.Select(entry => entry.Event.Sequence).Should().Equal(1, 2, 3, 4, 5);
        sessionHub.Published.Should().OnlyContain(entry =>
            entry.RootActorId == context.RootActorId && entry.SessionId == context.SessionId);
    }

    private static EventEnvelope CommittedEnvelope(string actorId, IMessage evt) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Payload = Any.Pack(new CommittedStateEventPublished
        {
            StateEvent = new StateEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                Version = 1,
                EventData = Any.Pack(evt),
                Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            StateRoot = Any.Pack(new RoleGAgentState()),
        }),
        Route = EnvelopeRouteSemantics.CreateObserverPublication(actorId),
        Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
    };

    private sealed class RecordingSessionEventHub : IProjectionSessionEventHub<AGUIEvent>
    {
        public List<(string RootActorId, string SessionId, AGUIEvent Event)> Published { get; } = [];

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            AGUIEvent evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add((rootActorId, sessionId, evt));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<AGUIEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = sessionId;
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(new NoopSubscription());
        }
    }

    private sealed class NoopSubscription : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
