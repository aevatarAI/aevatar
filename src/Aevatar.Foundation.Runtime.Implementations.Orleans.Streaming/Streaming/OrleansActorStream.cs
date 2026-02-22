using Google.Protobuf.WellKnownTypes;
using Google.Protobuf.Reflection;
using Orleans.Runtime;
using Orleans.Streams;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Streaming;

internal sealed class OrleansActorStream : IStream
{
    private readonly string _streamId;
    private readonly string _streamNamespace;
    private readonly global::Orleans.Streams.IStreamProvider _streamProvider;

    public OrleansActorStream(
        string streamId,
        string streamNamespace,
        global::Orleans.Streams.IStreamProvider streamProvider)
    {
        _streamId = streamId;
        _streamNamespace = streamNamespace;
        _streamProvider = streamProvider;
    }

    public string StreamId => _streamId;

    public Task ProduceAsync<T>(T message, CancellationToken ct = default)
        where T : IMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        var envelope = message as EventEnvelope ?? new EventEnvelope
        {
            Id = Guid.NewGuid().ToString("N"),
            Timestamp = Timestamp.FromDateTime(DateTime.UtcNow),
            Payload = Any.Pack(message),
            Direction = EventDirection.Down,
        };

        return ResolveStream().OnNextAsync(envelope);
    }

    public async Task<IAsyncDisposable> SubscribeAsync<T>(Func<T, Task> handler, CancellationToken ct = default)
        where T : IMessage, new()
    {
        ArgumentNullException.ThrowIfNull(handler);

        var observer = new DelegateObserver<T>(handler);
        var handle = await ResolveStream().SubscribeAsync(observer);
        return new OrleansSubscriptionLease(handle);
    }

    private IAsyncStream<EventEnvelope> ResolveStream()
    {
        var id = global::Orleans.Runtime.StreamId.Create(_streamNamespace, _streamId);
        return _streamProvider.GetStream<EventEnvelope>(id);
    }

    private sealed class OrleansSubscriptionLease : IAsyncDisposable
    {
        private readonly StreamSubscriptionHandle<EventEnvelope> _handle;
        private int _disposed;

        public OrleansSubscriptionLease(StreamSubscriptionHandle<EventEnvelope> handle)
        {
            _handle = handle;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            await _handle.UnsubscribeAsync();
        }
    }

    private sealed class DelegateObserver<TMessage> : IAsyncObserver<EventEnvelope>
        where TMessage : IMessage, new()
    {
        private readonly Func<TMessage, Task> _handler;
        private readonly MessageDescriptor? _descriptor;

        public DelegateObserver(Func<TMessage, Task> handler)
        {
            _handler = handler;
            _descriptor = typeof(TMessage) == typeof(EventEnvelope) ? null : new TMessage().Descriptor;
        }

        public Task OnCompletedAsync() => Task.CompletedTask;

        public Task OnErrorAsync(Exception ex)
        {
            _ = ex;
            return Task.CompletedTask;
        }

        public async Task OnNextAsync(EventEnvelope item, StreamSequenceToken? token = null)
        {
            _ = token;

            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                await ((Func<EventEnvelope, Task>)(object)_handler)(item);
                return;
            }

            if (item.Payload == null || _descriptor == null || !item.Payload.Is(_descriptor))
                return;

            await _handler(item.Payload.Unpack<TMessage>());
        }
    }
}
