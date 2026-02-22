using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;
using Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class StreamProviderLifecycleManagerTests
{
    [Fact]
    public void RemoveStream_WhenProviderSupportsCacheEviction_ShouldRemoveCachedStream()
    {
        var streamProvider = new MassTransitKafkaStreamProvider(
            new NoOpKafkaEnvelopeTransport(),
            new MassTransitKafkaStreamOptions { StreamNamespace = "aevatar.test.events" });
        var first = streamProvider.GetStream("actor-1");
        var lifecycleManager = new StreamProviderLifecycleManager(streamProvider);

        lifecycleManager.RemoveStream("actor-1");
        var second = streamProvider.GetStream("actor-1");

        second.Should().NotBeSameAs(first);
    }

    [Fact]
    public void RemoveStream_WhenProviderDoesNotSupportCacheEviction_ShouldNotThrow()
    {
        var streamProvider = new StubStreamProvider();
        var lifecycleManager = new StreamProviderLifecycleManager(streamProvider);

        var act = () => lifecycleManager.RemoveStream("actor-1");

        act.Should().NotThrow();
    }

    private sealed class StubStreamProvider : IStreamProvider
    {
        public IStream GetStream(string actorId)
        {
            _ = actorId;
            return StubStream.Instance;
        }
    }

    private sealed class StubStream : IStream
    {
        public static StubStream Instance { get; } = new();

        public string StreamId => "stub";

        public Task ProduceAsync<T>(T message, CancellationToken ct = default)
            where T : Google.Protobuf.IMessage
        {
            _ = message;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : Google.Protobuf.IMessage, new()
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

    private sealed class NoOpKafkaEnvelopeTransport : IKafkaEnvelopeTransport
    {
        public Task PublishAsync(string streamNamespace, string streamId, byte[] payload, CancellationToken ct = default)
        {
            _ = streamNamespace;
            _ = streamId;
            _ = payload;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(Func<KafkaEnvelopeRecord, Task> handler, CancellationToken ct = default)
        {
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(NoOpSubscription.Instance);
        }
    }
}
