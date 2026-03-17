using System.Collections.Concurrent;
using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public sealed class MassTransitActorEventSubscriptionProvider : IActorEventSubscriptionProvider
{
    private readonly IMassTransitEnvelopeTransport _transport;
    private readonly MassTransitStreamOptions _options;
    private readonly ConcurrentDictionary<string, SubscriptionBucket> _subscribers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _transportSubscriptionGate = new(1, 1);
    private Task<IAsyncDisposable>? _transportSubscriptionTask;
    private long _nextSubscriberId;
    private int _subscriberCount;

    public MassTransitActorEventSubscriptionProvider(
        IMassTransitEnvelopeTransport transport,
        MassTransitStreamOptions options)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<IAsyncDisposable> SubscribeAsync<TMessage>(
        string actorId,
        Func<TMessage, Task> handler,
        CancellationToken ct = default)
        where TMessage : class, IMessage, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentNullException.ThrowIfNull(handler);
        ct.ThrowIfCancellationRequested();

        var subscriber = Subscriber.Create(
            Interlocked.Increment(ref _nextSubscriberId),
            handler);
        while (true)
        {
            var actorSubscribers = _subscribers.GetOrAdd(actorId, static _ => new SubscriptionBucket());
            if (actorSubscribers.TryAdd(subscriber))
                break;

            // A concurrent unsubscribe may have retired the previous empty bucket.
            // Remove the stale entry if it is still current and retry with a fresh bucket.
            _subscribers.TryRemove(new KeyValuePair<string, SubscriptionBucket>(actorId, actorSubscribers));
        }

        Interlocked.Increment(ref _subscriberCount);

        Task<IAsyncDisposable>? transportSubscriptionTask = null;

        try
        {
            transportSubscriptionTask = await EnsureTransportSubscriptionAsync(ct);
            await transportSubscriptionTask;
        }
        catch
        {
            RemoveSubscriber(actorId, subscriber.Id);
            if (transportSubscriptionTask != null)
                await ResetTransportSubscriptionIfCurrentAsync(transportSubscriptionTask);

            throw;
        }

        return new SubscriptionLease(this, actorId, subscriber.Id);
    }

    private async Task DispatchRecordAsync(MassTransitEnvelopeRecord record)
    {
        if (!string.Equals(record.StreamNamespace, _options.StreamNamespace, StringComparison.Ordinal) ||
            record.Payload is not { Length: > 0 })
        {
            return;
        }

        if (!_subscribers.TryGetValue(record.StreamId, out var actorSubscribers))
            return;

        var subscribers = actorSubscribers.Snapshot();
        if (subscribers.Length == 0)
            return;

        EventEnvelope envelope;
        try
        {
            envelope = EventEnvelope.Parser.ParseFrom(record.Payload);
        }
        catch
        {
            return;
        }

        foreach (var subscriber in subscribers)
        {
            await subscriber.DispatchAsync(envelope);
        }
    }

    private async ValueTask UnsubscribeAsync(string actorId, long subscriberId)
    {
        if (!RemoveSubscriber(actorId, subscriberId))
            return;

        if (Volatile.Read(ref _subscriberCount) == 0)
            await ReleaseTransportSubscriptionIfIdleAsync();
    }

    private async ValueTask<Task<IAsyncDisposable>> EnsureTransportSubscriptionAsync(CancellationToken ct)
    {
        var currentTask = Volatile.Read(ref _transportSubscriptionTask);
        if (currentTask != null)
            return currentTask;

        await _transportSubscriptionGate.WaitAsync(ct);
        try
        {
            currentTask = _transportSubscriptionTask;
            if (currentTask != null)
                return currentTask;

            currentTask = _transport.SubscribeAsync(DispatchRecordAsync, ct);
            Volatile.Write(ref _transportSubscriptionTask, currentTask);
            return currentTask;
        }
        finally
        {
            _transportSubscriptionGate.Release();
        }
    }

    private async Task ResetTransportSubscriptionIfCurrentAsync(Task<IAsyncDisposable> transportSubscriptionTask)
    {
        await _transportSubscriptionGate.WaitAsync();
        try
        {
            if (ReferenceEquals(_transportSubscriptionTask, transportSubscriptionTask))
                _transportSubscriptionTask = null;
        }
        finally
        {
            _transportSubscriptionGate.Release();
        }
    }

    private async Task ReleaseTransportSubscriptionIfIdleAsync()
    {
        await _transportSubscriptionGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _subscriberCount) != 0)
                return;

            var transportSubscriptionTask = _transportSubscriptionTask;
            _transportSubscriptionTask = null;
            if (transportSubscriptionTask is not { IsCompletedSuccessfully: true })
                return;

            await (await transportSubscriptionTask).DisposeAsync();
        }
        finally
        {
            _transportSubscriptionGate.Release();
        }
    }

    private bool RemoveSubscriber(string actorId, long subscriberId)
    {
        if (!_subscribers.TryGetValue(actorId, out var actorSubscribers))
        {
            return false;
        }

        var removalResult = actorSubscribers.TryRemove(subscriberId);
        if (removalResult == SubscriptionBucketRemovalResult.NotFound)
            return false;

        Interlocked.Decrement(ref _subscriberCount);
        if (removalResult == SubscriptionBucketRemovalResult.RemovedLastSubscriber &&
            actorSubscribers.TryRetireIfEmpty())
        {
            _subscribers.TryRemove(new KeyValuePair<string, SubscriptionBucket>(actorId, actorSubscribers));
        }

        return true;
    }

    private sealed class Subscriber
    {
        public long Id { get; }
        private readonly Func<EventEnvelope, Task> _dispatchAsync;

        private Subscriber(long id, Func<EventEnvelope, Task> dispatchAsync)
        {
            Id = id;
            _dispatchAsync = dispatchAsync;
        }

        public Task DispatchAsync(EventEnvelope envelope) => _dispatchAsync(envelope);

        public static Subscriber Create<TMessage>(long id, Func<TMessage, Task> handler)
            where TMessage : class, IMessage, new()
        {
            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                return new Subscriber(id, envelope => ((Func<EventEnvelope, Task>)(object)handler)(envelope));
            }

            var descriptor = new TMessage().Descriptor;
            return new Subscriber(id, envelope =>
            {
                if (envelope.Payload == null || !envelope.Payload.Is(descriptor))
                    return Task.CompletedTask;

                return handler(envelope.Payload.Unpack<TMessage>());
            });
        }
    }

    private sealed class SubscriptionBucket
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<long, Subscriber> _subscribers = [];
        private bool _isRetired;

        public bool TryAdd(Subscriber subscriber)
        {
            ArgumentNullException.ThrowIfNull(subscriber);

            lock (_gate)
            {
                if (_isRetired)
                    return false;

                _subscribers[subscriber.Id] = subscriber;
                return true;
            }
        }

        public SubscriptionBucketRemovalResult TryRemove(long subscriberId)
        {
            lock (_gate)
            {
                if (!_subscribers.Remove(subscriberId))
                    return SubscriptionBucketRemovalResult.NotFound;

                return _subscribers.Count == 0
                    ? SubscriptionBucketRemovalResult.RemovedLastSubscriber
                    : SubscriptionBucketRemovalResult.Removed;
            }
        }

        public bool TryRetireIfEmpty()
        {
            lock (_gate)
            {
                if (_isRetired || _subscribers.Count != 0)
                    return false;

                _isRetired = true;
                return true;
            }
        }

        public Subscriber[] Snapshot()
        {
            lock (_gate)
            {
                return _subscribers.Count == 0 ? [] : _subscribers.Values.ToArray();
            }
        }
    }

    private sealed class SubscriptionLease : IAsyncDisposable
    {
        private readonly MassTransitActorEventSubscriptionProvider _owner;
        private readonly string _actorId;
        private readonly long _subscriberId;
        private int _disposed;

        public SubscriptionLease(
            MassTransitActorEventSubscriptionProvider owner,
            string actorId,
            long subscriberId)
        {
            _owner = owner;
            _actorId = actorId;
            _subscriberId = subscriberId;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return ValueTask.CompletedTask;

            return new ValueTask(_owner.UnsubscribeAsync(_actorId, _subscriberId).AsTask());
        }
    }
}

internal enum SubscriptionBucketRemovalResult
{
    NotFound = 0,
    Removed = 1,
    RemovedLastSubscriber = 2,
}
