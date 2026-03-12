using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Core.Propagation;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Actors;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public class OrleansGrainEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_WhenDirectionIsSelf_ShouldEnqueueToOwnStreamWithoutPublisherChain()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(actorId: "actor-self", streams: streams);

        await publisher.PublishAsync(new StringValue { Value = "hello" }, EventDirection.Self, CancellationToken.None);

        var delivered = streams.GetProduced("actor-self").Should().ContainSingle().Subject;
        delivered.Payload!.Unpack<StringValue>().Value.Should().Be("hello");
        delivered.Runtime?.VisitedActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_WhenSourceContainsPublisherChain_ShouldAppendCurrentPublisher()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(
            actorId: "child-actor",
            streams: streams,
            getParentId: () => "parent-actor");

        var inbound = new EventEnvelope();
        VisitedActorChain.AppendIfMissing(inbound, "parent-actor");

        await publisher.PublishAsync(
            new StringValue { Value = "reply" },
            EventDirection.Up,
            CancellationToken.None,
            inbound);

        var delivered = streams.GetProduced("parent-actor").Should().ContainSingle().Subject;
        delivered.Runtime!.VisitedActorIds.Should().ContainSingle().Which.Should().Be("child-actor");
    }

    [Fact]
    public async Task SendToAsync_WhenTargetIsSelf_ShouldEnqueueToOwnStreamWithoutPublisherChain()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(actorId: "actor-self", streams: streams);

        await publisher.SendToAsync("actor-self", new StringValue { Value = "direct" }, CancellationToken.None);

        var delivered = streams.GetProduced("actor-self").Should().ContainSingle().Subject;
        delivered.Payload!.Unpack<StringValue>().Value.Should().Be("direct");
        delivered.Route!.TargetActorId.Should().Be("actor-self");
        delivered.Runtime?.VisitedActorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SendToAsync_WhenSourceContainsPublisherChain_ShouldAppendCurrentPublisher()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(actorId: "sender", streams: streams);

        var inbound = new EventEnvelope();
        VisitedActorChain.AppendIfMissing(inbound, "upstream");

        await publisher.SendToAsync(
            "receiver",
            new StringValue { Value = "direct" },
            CancellationToken.None,
            inbound);

        var delivered = streams.GetProduced("receiver").Should().ContainSingle().Subject;
        delivered.Runtime!.VisitedActorIds.Should().ContainSingle().Which.Should().Be("sender");
    }

    [Fact]
    public async Task SendToAsync_ShouldDispatchViaStreamProvider()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(actorId: "sender", streams: streams);

        await publisher.SendToAsync("receiver", new StringValue { Value = "stream" }, CancellationToken.None);

        var delivered = streams.GetProduced("receiver").Should().ContainSingle().Subject;
        delivered.Payload!.Unpack<StringValue>().Value.Should().Be("stream");
        delivered.Runtime!.VisitedActorIds.Should().ContainSingle().Which.Should().Be("sender");
    }

    [Fact]
    public async Task PublishAsync_WhenDirectionIsDown_ShouldRouteByForwardingRegistry()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new RecordingStreamProvider(registry);
        await registry.UpsertAsync(
            new StreamForwardingBinding
            {
                SourceStreamId = "root",
                TargetStreamId = "child-a",
                ForwardingMode = StreamForwardingMode.HandleThenForward,
                DirectionFilter =
                [
                    EventDirection.Down,
                    EventDirection.Both,
                ],
            },
            CancellationToken.None);

        var publisher = CreatePublisher(actorId: "root", streams: streams);

        await publisher.PublishAsync(new StringValue { Value = "task" }, EventDirection.Down, CancellationToken.None);

        var childAEnvelope = streams.GetProduced("child-a").Should().ContainSingle().Subject;
        childAEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("task");
        StreamForwardingEnvelopeState.GetSourceStreamId(childAEnvelope).Should().Be("root");
        StreamForwardingEnvelopeState.GetTargetStreamId(childAEnvelope).Should().Be("child-a");
        StreamForwardingEnvelopeState.GetMode(childAEnvelope).Should().Be(StreamForwardingHandleMode.HandleThenForward);
        streams.GetProduced("child-b").Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_WhenTransitOnlyBindingConfigured_ShouldSkipTransitActorAndReachLeaf()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new RecordingStreamProvider(registry);
        await registry.UpsertAsync(
            new StreamForwardingBinding
            {
                SourceStreamId = "root",
                TargetStreamId = "middle",
                ForwardingMode = StreamForwardingMode.TransitOnly,
                DirectionFilter =
                [
                    EventDirection.Down,
                    EventDirection.Both,
                ],
            },
            CancellationToken.None);
        await registry.UpsertAsync(
            new StreamForwardingBinding
            {
                SourceStreamId = "middle",
                TargetStreamId = "leaf",
                ForwardingMode = StreamForwardingMode.HandleThenForward,
                DirectionFilter =
                [
                    EventDirection.Down,
                    EventDirection.Both,
                ],
            },
            CancellationToken.None);

        var publisher = CreatePublisher(actorId: "root", streams: streams);

        await publisher.PublishAsync(new StringValue { Value = "transit" }, EventDirection.Down, CancellationToken.None);

        streams.GetProduced("middle").Should().BeEmpty();
        var leafEnvelope = streams.GetProduced("leaf").Should().ContainSingle().Subject;
        leafEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("transit");
        StreamForwardingEnvelopeState.GetSourceStreamId(leafEnvelope).Should().Be("middle");
        StreamForwardingEnvelopeState.GetTargetStreamId(leafEnvelope).Should().Be("leaf");
        StreamForwardingEnvelopeState.GetMode(leafEnvelope).Should().Be(StreamForwardingHandleMode.HandleThenForward);
    }

    [Fact]
    public async Task PublishAsync_WhenForwardingGraphContainsCycle_ShouldSkipLoopbackTarget()
    {
        var registry = new InMemoryStreamForwardingRegistry();
        var streams = new RecordingStreamProvider(registry);
        await registry.UpsertAsync(StreamForwardingRules.CreateHierarchyBinding("root", "middle"), CancellationToken.None);
        await registry.UpsertAsync(StreamForwardingRules.CreateHierarchyBinding("middle", "root"), CancellationToken.None);

        var publisher = CreatePublisher(actorId: "root", streams: streams);

        await publisher.PublishAsync(new StringValue { Value = "cycle" }, EventDirection.Down, CancellationToken.None);

        var middleEnvelope = streams.GetProduced("middle").Should().ContainSingle().Subject;
        streams.GetProduced("root").Should().ContainSingle();
        middleEnvelope.Runtime?.VisitedActorIds.Should().BeEmpty();
    }

    private static OrleansGrainEventPublisher CreatePublisher(
        string actorId,
        RecordingStreamProvider streams,
        Func<string?>? getParentId = null)
    {
        return new OrleansGrainEventPublisher(
            actorId,
            getParentId ?? (() => null),
            new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy()),
            streams);
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Lock _lock = new();
        private readonly IStreamForwardingRegistry _registry;
        private readonly Dictionary<string, RecordingStream> _streams = new(StringComparer.Ordinal);

        public RecordingStreamProvider(IStreamForwardingRegistry? registry = null)
        {
            _registry = registry ?? new InMemoryStreamForwardingRegistry();
        }

        public IStream GetStream(string actorId)
        {
            lock (_lock)
            {
                if (!_streams.TryGetValue(actorId, out var stream))
                {
                    stream = new RecordingStream(actorId, _registry, this);
                    _streams[actorId] = stream;
                }

                return stream;
            }
        }

        public void Append(string actorId, EventEnvelope envelope)
        {
            var stream = (RecordingStream)GetStream(actorId);
            stream.Messages.Add(envelope.Clone());
        }

        public IReadOnlyList<EventEnvelope> GetProduced(string actorId)
        {
            lock (_lock)
            {
                return _streams.TryGetValue(actorId, out var stream)
                    ? stream.Messages.ToList()
                    : [];
            }
        }
    }

    private sealed class RecordingStream(
        string streamId,
        IStreamForwardingRegistry registry,
        RecordingStreamProvider owner) : IStream
    {
        public string StreamId => streamId;

        public List<EventEnvelope> Messages { get; } = [];

        public async Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            ct.ThrowIfCancellationRequested();

            var envelope = message as EventEnvelope ?? new EventEnvelope
            {
                Payload = Any.Pack(message),
            };

            Messages.Add(envelope.Clone());
            await RelayAsync(StreamId, envelope, ct);
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(NoOpSubscription.Instance);
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return registry.UpsertAsync(new StreamForwardingBinding
            {
                SourceStreamId = StreamId,
                TargetStreamId = binding.TargetStreamId,
                ForwardingMode = binding.ForwardingMode,
                DirectionFilter = new HashSet<EventDirection>(binding.DirectionFilter),
                EventTypeFilter = new HashSet<string>(binding.EventTypeFilter, StringComparer.Ordinal),
                Version = binding.Version,
                LeaseId = binding.LeaseId,
            }, ct);
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return registry.RemoveAsync(StreamId, targetStreamId, ct);
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return registry.ListBySourceAsync(StreamId, ct);
        }

        private async Task RelayAsync(string sourceStreamId, EventEnvelope envelope, CancellationToken ct)
        {
            var queue = new Queue<(string SourceStreamId, EventEnvelope Envelope)>();
            var visitedSources = new HashSet<string>(StringComparer.Ordinal);
            queue.Enqueue((sourceStreamId, envelope));

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (currentSourceId, currentEnvelope) = queue.Dequeue();
                if (!visitedSources.Add(currentSourceId))
                    continue;

                var bindings = await registry.ListBySourceAsync(currentSourceId, ct);
                foreach (var binding in bindings)
                {
                    if (!StreamForwardingRules.TryBuildForwardedEnvelope(
                            currentSourceId,
                            binding,
                            currentEnvelope,
                            out var forwarded) ||
                        forwarded == null)
                    {
                        continue;
                    }

                    queue.Enqueue((binding.TargetStreamId, forwarded));

                    if (binding.ForwardingMode == StreamForwardingMode.TransitOnly)
                        continue;

                    owner.Append(binding.TargetStreamId, forwarded);
                }
            }
        }
    }

    private sealed class NoOpSubscription : IAsyncDisposable
    {
        public static NoOpSubscription Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
