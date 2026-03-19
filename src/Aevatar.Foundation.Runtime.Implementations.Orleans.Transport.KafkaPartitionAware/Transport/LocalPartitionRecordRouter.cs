namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.KafkaPartitionAware;

internal sealed class LocalPartitionRecordRouter : ILocalDeliveryAckPort
{
    private static readonly TimeSpan NoHandlerRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly Lock _lock = new();
    private Dictionary<int, List<Func<PartitionEnvelopeRecord, Task>>> _handlers = [];

    public Task<IAsyncDisposable> SubscribeAsync(
        int partitionId,
        Func<PartitionEnvelopeRecord, Task> handler,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            var next = new Dictionary<int, List<Func<PartitionEnvelopeRecord, Task>>>(_handlers);
            if (!next.TryGetValue(partitionId, out var handlers))
                handlers = [];
            else
                handlers = [.. handlers];

            handlers.Add(handler);
            next[partitionId] = handlers;
            _handlers = next;
        }

        return Task.FromResult<IAsyncDisposable>(new CallbackSubscription(() => RemoveHandler(partitionId, handler)));
    }

    public async Task DeliverAsync(
        int partitionId,
        PartitionEnvelopeRecord record,
        CancellationToken ct = default)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            List<Func<PartitionEnvelopeRecord, Task>> handlers;
            lock (_lock)
            {
                handlers = _handlers.TryGetValue(partitionId, out var current)
                    ? [.. current]
                    : [];
            }

            if (handlers.Count > 0)
            {
                foreach (var handler in handlers)
                    await handler(record);

                return;
            }

            await Task.Delay(NoHandlerRetryDelay, ct);
        }
    }

    private void RemoveHandler(int partitionId, Func<PartitionEnvelopeRecord, Task> handler)
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(partitionId, out var current))
                return;

            var handlers = current.Where(x => x != handler).ToList();
            var next = new Dictionary<int, List<Func<PartitionEnvelopeRecord, Task>>>(_handlers);
            if (handlers.Count == 0)
                next.Remove(partitionId);
            else
                next[partitionId] = handlers;
            _handlers = next;
        }
    }

    private sealed class CallbackSubscription : IAsyncDisposable
    {
        private readonly Action _dispose;
        private int _disposed;

        public CallbackSubscription(Action dispose)
        {
            _dispose = dispose;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return ValueTask.CompletedTask;

            _dispose();
            return ValueTask.CompletedTask;
        }
    }
}
