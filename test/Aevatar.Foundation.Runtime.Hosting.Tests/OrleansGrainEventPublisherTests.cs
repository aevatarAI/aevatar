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
    public async Task PublishAsync_WhenDirectionIsSelf_ShouldDispatchWithoutPublisherChain()
    {
        EventEnvelope? dispatched = null;
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(
            actorId: "actor-self",
            streams: streams,
            onDispatchToSelf: envelope =>
            {
                dispatched = envelope;
                return Task.CompletedTask;
            });

        await publisher.PublishAsync(new StringValue { Value = "hello" }, EventDirection.Self, CancellationToken.None);

        dispatched.Should().NotBeNull();
        dispatched!.Metadata.ContainsKey(PublisherChainMetadata.PublishersMetadataKey).Should().BeFalse();
        streams.GetProduced("actor-self").Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_WhenSourceContainsPublisherChain_ShouldAppendCurrentPublisher()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(
            actorId: "child-actor",
            streams: streams,
            onDispatchToSelf: _ => Task.CompletedTask,
            getParentId: () => "parent-actor");

        var inbound = new EventEnvelope();
        inbound.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "parent-actor";

        await publisher.PublishAsync(
            new StringValue { Value = "reply" },
            EventDirection.Up,
            CancellationToken.None,
            inbound);

        var delivered = streams.GetProduced("parent-actor").Should().ContainSingle().Subject;
        delivered.Metadata.TryGetValue(PublisherChainMetadata.PublishersMetadataKey, out var chain)
            .Should().BeTrue();
        chain.Should().Be("parent-actor,child-actor");
    }

    [Fact]
    public async Task SendToAsync_WhenTargetIsSelf_ShouldDispatchWithoutPublisherChain()
    {
        EventEnvelope? dispatched = null;
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(
            actorId: "actor-self",
            streams: streams,
            onDispatchToSelf: envelope =>
            {
                dispatched = envelope;
                return Task.CompletedTask;
            });

        await publisher.SendToAsync("actor-self", new StringValue { Value = "direct" }, CancellationToken.None);

        dispatched.Should().NotBeNull();
        dispatched!.Metadata.ContainsKey(PublisherChainMetadata.PublishersMetadataKey).Should().BeFalse();
        streams.GetProduced("actor-self").Should().BeEmpty();
    }

    [Fact]
    public async Task SendToAsync_WhenSourceContainsPublisherChain_ShouldAppendCurrentPublisher()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(
            actorId: "sender",
            streams: streams,
            onDispatchToSelf: _ => Task.CompletedTask);

        var inbound = new EventEnvelope();
        inbound.Metadata[PublisherChainMetadata.PublishersMetadataKey] = "upstream";

        await publisher.SendToAsync(
            "receiver",
            new StringValue { Value = "direct" },
            CancellationToken.None,
            inbound);

        var delivered = streams.GetProduced("receiver").Should().ContainSingle().Subject;
        delivered.Metadata.TryGetValue(PublisherChainMetadata.PublishersMetadataKey, out var chain)
            .Should().BeTrue();
        chain.Should().Be("upstream,sender");
    }

    [Fact]
    public async Task SendToAsync_ShouldDispatchViaStreamProvider()
    {
        var streams = new RecordingStreamProvider();
        var publisher = CreatePublisher(
            actorId: "sender",
            streams: streams,
            onDispatchToSelf: _ => Task.CompletedTask);

        await publisher.SendToAsync("receiver", new StringValue { Value = "stream" }, CancellationToken.None);

        var delivered = streams.GetProduced("receiver").Should().ContainSingle().Subject;
        delivered.Payload!.Unpack<StringValue>().Value.Should().Be("stream");
        delivered.Metadata.TryGetValue(PublisherChainMetadata.PublishersMetadataKey, out var chain)
            .Should().BeTrue();
        chain.Should().Be("sender");
    }

    [Fact]
    public async Task PublishAsync_WhenDirectionIsDown_ShouldRouteByForwardingRegistry()
    {
        var streams = new RecordingStreamProvider();
        var registry = new InMemoryStreamForwardingRegistry();
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

        var publisher = CreatePublisher(
            actorId: "root",
            streams: streams,
            onDispatchToSelf: _ => Task.CompletedTask,
            forwardingRegistry: registry);

        await publisher.PublishAsync(new StringValue { Value = "task" }, EventDirection.Down, CancellationToken.None);

        var childAEnvelope = streams.GetProduced("child-a").Should().ContainSingle().Subject;
        childAEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("task");
        childAEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardSourceKey].Should().Be("root");
        childAEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey].Should().Be("child-a");
        childAEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey]
            .Should().Be(StreamForwardingEnvelopeMetadata.ForwardModeHandle);
        streams.GetProduced("child-b").Should().BeEmpty();
    }

    [Fact]
    public async Task PublishAsync_WhenTransitOnlyBindingConfigured_ShouldSkipTransitActorAndReachLeaf()
    {
        var streams = new RecordingStreamProvider();
        var registry = new InMemoryStreamForwardingRegistry();
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

        var publisher = CreatePublisher(
            actorId: "root",
            streams: streams,
            onDispatchToSelf: _ => Task.CompletedTask,
            forwardingRegistry: registry);

        await publisher.PublishAsync(new StringValue { Value = "transit" }, EventDirection.Down, CancellationToken.None);

        streams.GetProduced("middle").Should().BeEmpty();
        var leafEnvelope = streams.GetProduced("leaf").Should().ContainSingle().Subject;
        leafEnvelope.Payload!.Unpack<StringValue>().Value.Should().Be("transit");
        leafEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardSourceKey].Should().Be("middle");
        leafEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardTargetKey].Should().Be("leaf");
        leafEnvelope.Metadata[StreamForwardingEnvelopeMetadata.ForwardModeKey]
            .Should().Be(StreamForwardingEnvelopeMetadata.ForwardModeHandle);
    }

    [Fact]
    public async Task PublishAsync_WhenForwardingGraphContainsCycle_ShouldSkipLoopbackTarget()
    {
        var streams = new RecordingStreamProvider();
        var selfDispatchCount = 0;
        var registry = new InMemoryStreamForwardingRegistry();
        await registry.UpsertAsync(StreamForwardingRules.CreateHierarchyBinding("root", "middle"), CancellationToken.None);
        await registry.UpsertAsync(StreamForwardingRules.CreateHierarchyBinding("middle", "root"), CancellationToken.None);

        var publisher = CreatePublisher(
            actorId: "root",
            streams: streams,
            onDispatchToSelf: _ =>
            {
                Interlocked.Increment(ref selfDispatchCount);
                return Task.CompletedTask;
            },
            forwardingRegistry: registry);

        await publisher.PublishAsync(new StringValue { Value = "cycle" }, EventDirection.Down, CancellationToken.None);

        var middleEnvelope = streams.GetProduced("middle").Should().ContainSingle().Subject;
        selfDispatchCount.Should().Be(0);
        middleEnvelope.Metadata[PublisherChainMetadata.PublishersMetadataKey].Should().Be("root");
    }

    private static OrleansGrainEventPublisher CreatePublisher(
        string actorId,
        RecordingStreamProvider streams,
        Func<EventEnvelope, Task> onDispatchToSelf,
        Func<string?>? getParentId = null,
        IStreamForwardingRegistry? forwardingRegistry = null)
    {
        return new OrleansGrainEventPublisher(
            actorId,
            getParentId ?? (() => null),
            onDispatchToSelf,
            new DefaultEnvelopePropagationPolicy(new DefaultCorrelationLinkPolicy()),
            forwardingRegistry ?? new InMemoryStreamForwardingRegistry(),
            streams);
    }

    private sealed class RecordingStreamProvider : IStreamProvider
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<string, RecordingStream> _streams = new(StringComparer.Ordinal);

        public IStream GetStream(string actorId)
        {
            lock (_lock)
            {
                if (!_streams.TryGetValue(actorId, out var stream))
                {
                    stream = new RecordingStream(actorId);
                    _streams[actorId] = stream;
                }

                return stream;
            }
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

    private sealed class RecordingStream(string streamId) : IStream
    {
        public string StreamId => streamId;

        public List<EventEnvelope> Messages { get; } = [];

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            ct.ThrowIfCancellationRequested();

            var envelope = message as EventEnvelope ?? new EventEnvelope
            {
                Payload = Any.Pack(message),
            };

            Messages.Add(envelope.Clone());
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(NoOpSubscription.Instance);
        }
    }

    private sealed class NoOpSubscription : IAsyncDisposable
    {
        public static NoOpSubscription Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
