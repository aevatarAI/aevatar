using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Streaming;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Projection.Orchestration;
using Aevatar.GAgentService.Projection.Projectors;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.GAgentService.Tests.Projection;

public sealed class LlmSessionObservationSessionEventProjectorTests
{
    [Fact]
    public async Task ProjectAsync_ShouldRouteOnlyWhenEnvelopeCorrelationMatchesSessionId()
    {
        var streams = new RecordingStreamProvider();
        var hub = new LlmSessionObservationSessionEventHub(
            streams,
            new LlmSessionObservationSessionEventCodec(),
            NullLogger<Aevatar.CQRS.Projection.Core.Streaming.ProjectionSessionEventHub<EventEnvelope>>.Instance);
        var projector = new LlmSessionObservationSessionEventProjector(hub);
        var context = new LlmSessionObservationProjectionContext
        {
            RootActorId = "actor-1",
            SessionId = "resp-1",
            ProjectionKind = "llm-session-observation",
        };

        await projector.ProjectAsync(context, CommittedEnvelope("resp-1", RunStartedPayload()));
        await projector.ProjectAsync(context, CommittedEnvelope("resp-1:llm-run", RunStartedPayload()));

        var stream = streams.Streams.Should().ContainSingle().Subject.Value;
        stream.Produced.Should().ContainSingle();
        var transport = stream.Produced[0];
        transport.RootActorId.Should().Be("actor-1");
        transport.SessionId.Should().Be("resp-1");
        var routed = EventEnvelope.Parser.ParseFrom(transport.Payload);
        routed.Propagation!.CorrelationId.Should().Be("resp-1");
    }

    private static LlmRunStartedEvent RunStartedPayload() =>
        new()
        {
            ResponseId = "resp-1",
            RunId = "resp-1:llm-run",
            Sequence = 1,
            StartedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        };

    private static EventEnvelope CommittedEnvelope(string correlationId, IMessage payload)
    {
        return new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Payload = Any.Pack(new CommittedStateEventPublished
            {
                StateEvent = new StateEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    Version = 1,
                    EventData = Any.Pack(payload),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                },
            }),
            Propagation = new EnvelopePropagation { CorrelationId = correlationId },
        };
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        public Dictionary<string, RecordingStream> Streams { get; } = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId)
        {
            if (!Streams.TryGetValue(actorId, out var stream))
            {
                stream = new RecordingStream(actorId);
                Streams[actorId] = stream;
            }

            return stream;
        }
    }

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId { get; } = streamId;

        public List<ProjectionSessionEventTransportMessage> Produced { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : IMessage
        {
            if (message is not ProjectionSessionEventTransportMessage transport)
                throw new NotSupportedException($"Unsupported message type '{typeof(T).Name}'.");

            Produced.Add(transport);
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(
            Func<T, Task> handler,
            CancellationToken ct = default) where T : IMessage, new() =>
            Task.FromResult<IAsyncDisposable>(new NoOpAsyncDisposable());

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
