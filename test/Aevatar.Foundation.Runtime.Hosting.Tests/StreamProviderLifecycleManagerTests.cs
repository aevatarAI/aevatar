using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;
using FluentAssertions;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class StreamProviderLifecycleManagerTests
{
    [Fact]
    public void RemoveStream_WhenUsingMassTransitKafkaLifecycleManager_ShouldRemoveCachedStream()
    {
        var streamProvider = new MassTransitStreamProvider(
            new NoOpKafkaEnvelopeTransport(),
            new MassTransitStreamOptions { StreamNamespace = "aevatar.test.events" });
        var first = streamProvider.GetStream("actor-1");
        var lifecycleManager = new MassTransitStreamLifecycleManager(streamProvider);

        lifecycleManager.RemoveStream("actor-1");
        var second = streamProvider.GetStream("actor-1");

        second.Should().NotBeSameAs(first);
    }

    private sealed class NoOpKafkaEnvelopeTransport : IMassTransitEnvelopeTransport
    {
        public Task PublishAsync(
            string streamNamespace,
            string streamId,
            byte[] payload,
            CancellationToken ct = default)
        {
            _ = streamNamespace;
            _ = streamId;
            _ = payload;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync(Func<MassTransitEnvelopeRecord, Task> handler, CancellationToken ct = default)
        {
            _ = handler;
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IAsyncDisposable>(NoOpAsyncDisposable.Instance);
        }
    }

    private sealed class NoOpAsyncDisposable : IAsyncDisposable
    {
        public static NoOpAsyncDisposable Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
