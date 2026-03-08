using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Aevatar.Foundation.Runtime.Streaming;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Tests;

public sealed class RuntimeStreamRequestReplyClientTests
{
    private const string ReadyProbePrefix = "__aevatar.reply.ready__:";

    [Fact]
    public async Task QueryAsync_ShouldPrimeReplyStreamBeforeDispatch()
    {
        var streams = new DeferredReplySubscriptionStreamProvider();
        var client = new RuntimeStreamRequestReplyClient();

        var response = await client.QueryAsync<Int32Value>(
            streams,
            "workflow.definition.catalog.names.reply",
            TimeSpan.FromSeconds(2),
            async (_, replyStreamId) =>
            {
                await streams.GetStream(replyStreamId).ProduceAsync(new Int32Value { Value = 42 });
            },
            static (reply, _) => reply.Value == 42,
            static requestId => $"query timed out. request_id={requestId}");

        response.Value.Should().Be(42);
        streams.ReadyProbeObserved.Should().BeTrue();
    }

    private sealed class DeferredReplySubscriptionStreamProvider : IStreamProvider
    {
        private readonly ConcurrentDictionary<string, DeferredReplySubscriptionStream> _streams = new(StringComparer.Ordinal);

        public bool ReadyProbeObserved => _streams.Values.Any(stream => stream.ReadyProbeObserved);

        public IStream GetStream(string actorId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
            return _streams.GetOrAdd(actorId, static id => new DeferredReplySubscriptionStream(id));
        }
    }

    private sealed class DeferredReplySubscriptionStream : IStream
    {
        private readonly List<Func<EventEnvelope, Task>> _envelopeHandlers = [];
        private bool _deliveryEnabled;

        public DeferredReplySubscriptionStream(string streamId)
        {
            StreamId = streamId;
        }

        public string StreamId { get; }

        public bool ReadyProbeObserved { get; private set; }

        public Task ProduceAsync<T>(T message, CancellationToken ct = default) where T : IMessage
        {
            ArgumentNullException.ThrowIfNull(message);
            ct.ThrowIfCancellationRequested();

            var envelope = message as EventEnvelope ?? new EventEnvelope
            {
                Id = Guid.NewGuid().ToString("N"),
                Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
                Payload = Any.Pack(message),
                Direction = EventDirection.Down,
            };

            if (IsReadyProbe(envelope))
            {
                ReadyProbeObserved = true;
                _deliveryEnabled = true;
                return DeliverAsync(envelope);
            }

            return _deliveryEnabled ? DeliverAsync(envelope) : Task.CompletedTask;
        }

        public Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
            where T : IMessage, new()
        {
            ArgumentNullException.ThrowIfNull(handler);
            ct.ThrowIfCancellationRequested();

            if (typeof(T) != typeof(EventEnvelope))
                throw new NotSupportedException("DeferredReplySubscriptionStream only supports EventEnvelope subscriptions.");

            Task WrappedHandler(EventEnvelope envelope) => handler((T)(IMessage)envelope);
            _envelopeHandlers.Add(WrappedHandler);
            return Task.FromResult<IAsyncDisposable>(new Subscription(() => _envelopeHandlers.Remove(WrappedHandler)));
        }

        public Task UpsertRelayAsync(StreamForwardingBinding binding, CancellationToken ct = default)
        {
            _ = binding;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task RemoveRelayAsync(string targetStreamId, CancellationToken ct = default)
        {
            _ = targetStreamId;
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StreamForwardingBinding>> ListRelaysAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<StreamForwardingBinding>>([]);
        }

        private static bool IsReadyProbe(EventEnvelope envelope)
        {
            if (envelope.Payload == null || !envelope.Payload.Is(StringValue.Descriptor))
                return false;

            return envelope.Payload.Unpack<StringValue>().Value.StartsWith(ReadyProbePrefix, StringComparison.Ordinal);
        }

        private Task DeliverAsync(EventEnvelope envelope)
        {
            if (_envelopeHandlers.Count == 0)
                return Task.CompletedTask;

            return Task.WhenAll(_envelopeHandlers.Select(handler => handler(envelope)));
        }

        private sealed class Subscription : IAsyncDisposable
        {
            private readonly Action _dispose;
            private int _disposed;

            public Subscription(Action dispose)
            {
                _dispose = dispose;
            }

            public ValueTask DisposeAsync()
            {
                if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
                    _dispose();

                return ValueTask.CompletedTask;
            }
        }
    }
}
