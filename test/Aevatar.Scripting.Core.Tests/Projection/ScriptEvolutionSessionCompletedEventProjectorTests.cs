using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public class ScriptEvolutionSessionCompletedEventProjectorTests
{
    [Fact]
    public async Task Should_Publish_SessionCompleted_To_ProjectionSessionHub()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ScriptEvolutionSessionCompletedEventProjector(hub);
        var context = new ScriptEvolutionSessionProjectionContext
        {
            ProjectionId = "script-evolution-session:proposal-1",
            RootActorId = "script-evolution-session:proposal-1",
            ProposalId = "proposal-1",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new ScriptEvolutionSessionCompletedEvent
                {
                    ProposalId = "proposal-1",
                    Accepted = true,
                    Status = "promoted",
                },
                version: 1,
                id: "evt-1"),
            CancellationToken.None);

        hub.Published.Should().ContainSingle();
        hub.Published[0].ScopeId.Should().Be("script-evolution-session:proposal-1");
        hub.Published[0].SessionId.Should().Be("proposal-1");
        hub.Published[0].Event.ProposalId.Should().Be("proposal-1");
        hub.Published[0].Event.Accepted.Should().BeTrue();
    }

    [Fact]
    public async Task Should_Ignore_NonSessionCompleted_Event()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ScriptEvolutionSessionCompletedEventProjector(hub);
        var context = new ScriptEvolutionSessionProjectionContext
        {
            ProjectionId = "script-evolution-session:proposal-2",
            RootActorId = "script-evolution-session:proposal-2",
            ProposalId = "proposal-2",
        };

        await projector.ProjectAsync(
            context,
            WrapCommitted(
                new ScriptEvolutionSessionStartedEvent
                {
                    ProposalId = "proposal-2",
                },
                version: 2,
                id: "evt-2"),
            CancellationToken.None);

        hub.Published.Should().BeEmpty();
    }

    private sealed class RecordingSessionEventHub : IProjectionSessionEventHub<ScriptEvolutionSessionCompletedEvent>
    {
        public List<PublishedMessage> Published { get; } = [];

        public Task PublishAsync(
            string scopeId,
            string sessionId,
            ScriptEvolutionSessionCompletedEvent evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(new PublishedMessage(scopeId, sessionId, evt.Clone()));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string scopeId,
            string sessionId,
            Func<ScriptEvolutionSessionCompletedEvent, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = scopeId;
            _ = sessionId;
            _ = handler;
            _ = ct;
            throw new NotSupportedException();
        }
    }

    private sealed record PublishedMessage(
        string ScopeId,
        string SessionId,
        ScriptEvolutionSessionCompletedEvent Event);

    private static EventEnvelope WrapCommitted(
        IMessage evt,
        long version,
        string id,
        DateTime? utcTimestamp = null)
    {
        var occurredAt = Timestamp.FromDateTime((utcTimestamp ?? DateTime.UtcNow).ToUniversalTime());
        return new EventEnvelope
        {
            Id = id,
            Timestamp = occurredAt.Clone(),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("script-evolution-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = id,
                    Version = version,
                    Timestamp = occurredAt,
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new Empty()),
            }),
        };
    }
}
