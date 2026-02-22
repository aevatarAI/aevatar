using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aevatar.Foundation.Runtime.Implementations.Orleans.Transport.Kafka;

internal sealed class KafkaEnvelopeDispatcher
{
    private readonly Lock _handlersLock = new();
    private volatile Func<KafkaEnvelopeRecord, Task>[] _handlers = [];
    private readonly ILogger<KafkaEnvelopeDispatcher> _logger;

    public KafkaEnvelopeDispatcher(ILogger<KafkaEnvelopeDispatcher>? logger = null)
    {
        _logger = logger ?? NullLogger<KafkaEnvelopeDispatcher>.Instance;
    }

    public Task<IAsyncDisposable> SubscribeAsync(Func<KafkaEnvelopeRecord, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_handlersLock)
        {
            var current = _handlers;
            var next = new Func<KafkaEnvelopeRecord, Task>[current.Length + 1];
            current.CopyTo(next, 0);
            next[current.Length] = handler;
            _handlers = next;
        }

        return Task.FromResult<IAsyncDisposable>(new Subscription(this, handler));
    }

    public async Task DispatchAsync(KafkaEnvelopeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var handlers = _handlers;
        if (handlers.Length == 0)
            return;

        List<Task>? tasks = null;
        foreach (var handler in handlers)
        {
            tasks ??= new List<Task>(handlers.Length);
            tasks.Add(DispatchOneAsyncWithLogging(handler, record));
        }

        if (tasks is { Count: > 0 })
            await Task.WhenAll(tasks);
    }

    private static async Task DispatchOneAsync(Func<KafkaEnvelopeRecord, Task> handler, KafkaEnvelopeRecord record)
    {
        await handler(record);
    }

    private async Task DispatchOneAsyncWithLogging(Func<KafkaEnvelopeRecord, Task> handler, KafkaEnvelopeRecord record)
    {
        try
        {
            await DispatchOneAsync(handler, record);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Kafka envelope dispatch failed for stream {StreamNamespace}/{StreamId}.",
                record.StreamNamespace,
                record.StreamId);
            throw;
        }
    }

    private void Unsubscribe(Func<KafkaEnvelopeRecord, Task> handler)
    {
        lock (_handlersLock)
        {
            _handlers = _handlers.Where(x => x != handler).ToArray();
        }
    }

    private sealed class Subscription : IAsyncDisposable
    {
        private readonly KafkaEnvelopeDispatcher _owner;
        private readonly Func<KafkaEnvelopeRecord, Task> _handler;
        private int _disposed;

        public Subscription(KafkaEnvelopeDispatcher owner, Func<KafkaEnvelopeRecord, Task> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return ValueTask.CompletedTask;

            _owner.Unsubscribe(_handler);
            return ValueTask.CompletedTask;
        }
    }
}
