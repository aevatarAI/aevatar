using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Scripting.Abstractions;
using Aevatar.Scripting.Projection.Orchestration;
using Aevatar.Scripting.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Scripting.Core.Tests.Projection;

public sealed class ScriptExecutionSessionEventProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldPublishOutcomeEnvelope_WhenTypedOutcomeCorrelationMatchesSession()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ScriptExecutionSessionEventProjector(hub);
        var context = BuildContext("correlation-1");
        var envelope = WrapCommitted(new ScriptRunOutcomeRecordedEvent
        {
            ScriptRunId = "run-1",
            CommandId = "command-1",
            CorrelationId = "correlation-1",
            Status = ScriptRunOutcomeStatus.Succeeded,
        });

        await projector.ProjectAsync(context, envelope, CancellationToken.None);

        hub.Published.Should().ContainSingle();
        hub.Published[0].RootActorId.Should().Be("script-runtime:scope-1:script-1");
        hub.Published[0].SessionId.Should().Be("correlation-1");
        hub.Published[0].Event.Id.Should().Be("evt-1");
    }

    [Fact]
    public async Task ProjectAsync_ShouldIgnoreOutcomeEnvelope_WhenTypedOutcomeCorrelationDoesNotMatchSession()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ScriptExecutionSessionEventProjector(hub);
        var context = BuildContext("correlation-2");
        var envelope = WrapCommitted(new ScriptRunOutcomeRecordedEvent
        {
            ScriptRunId = "run-1",
            CommandId = "command-1",
            CorrelationId = "correlation-1",
            Status = ScriptRunOutcomeStatus.Succeeded,
        });

        await projector.ProjectAsync(context, envelope, CancellationToken.None);

        hub.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task ProjectAsync_ShouldPreferEnvelopePropagationCorrelation()
    {
        var hub = new RecordingSessionEventHub();
        var projector = new ScriptExecutionSessionEventProjector(hub);
        var context = BuildContext("propagation-correlation");
        var envelope = WrapCommitted(
            new ScriptRunOutcomeRecordedEvent
            {
                ScriptRunId = "run-1",
                CommandId = "command-1",
                CorrelationId = "typed-correlation",
                Status = ScriptRunOutcomeStatus.Succeeded,
            },
            propagationCorrelationId: "propagation-correlation");

        await projector.ProjectAsync(context, envelope, CancellationToken.None);

        hub.Published.Should().ContainSingle();
        hub.Published[0].SessionId.Should().Be("propagation-correlation");
    }

    private static ScriptExecutionProjectionContext BuildContext(string sessionId) =>
        new()
        {
            RootActorId = "script-runtime:scope-1:script-1",
            SessionId = sessionId,
            ProjectionKind = "script-execution-read-model",
        };

    private static EventEnvelope WrapCommitted(
        IMessage evt,
        string? propagationCorrelationId = null) =>
        new()
        {
            Id = "evt-1",
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Route = EnvelopeRouteSemantics.CreateObserverPublication("script-execution-test"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = "evt-1",
                    Version = 1,
                    Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                    EventData = Any.Pack(evt),
                },
                StateRoot = Any.Pack(new Empty()),
            }),
            Propagation = string.IsNullOrWhiteSpace(propagationCorrelationId)
                ? null
                : new EnvelopePropagation { CorrelationId = propagationCorrelationId },
        };

    private sealed class RecordingSessionEventHub : IProjectionSessionEventHub<EventEnvelope>
    {
        public List<PublishedMessage> Published { get; } = [];

        public Task PublishAsync(
            string rootActorId,
            string sessionId,
            EventEnvelope evt,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Published.Add(new PublishedMessage(rootActorId, sessionId, evt.Clone()));
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(
            string rootActorId,
            string sessionId,
            Func<EventEnvelope, ValueTask> handler,
            CancellationToken ct = default)
        {
            _ = rootActorId;
            _ = sessionId;
            _ = handler;
            _ = ct;
            throw new NotSupportedException();
        }
    }

    private sealed record PublishedMessage(
        string RootActorId,
        string SessionId,
        EventEnvelope Event);
}
