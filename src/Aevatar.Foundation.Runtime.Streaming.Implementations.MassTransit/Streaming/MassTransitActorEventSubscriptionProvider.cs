using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Streaming;
using Google.Protobuf;

namespace Aevatar.Foundation.Runtime.Streaming.Implementations.MassTransit;

public sealed class MassTransitActorEventSubscriptionProvider : IActorEventSubscriptionProvider
{
    private readonly IMassTransitEnvelopeTransport _transport;
    private readonly MassTransitStreamOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Subscriber>> _subscribers = new(StringComparer.Ordinal);
    private Task<IAsyncDisposable>? _transportSubscriptionTask;
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

        var subscriber = Subscriber.Create(handler);
        Task<IAsyncDisposable> transportSubscriptionTask;

        lock (_gate)
        {
            if (!_subscribers.TryGetValue(actorId, out var actorSubscribers))
            {
                actorSubscribers = [];
                _subscribers[actorId] = actorSubscribers;
            }

            actorSubscribers.Add(subscriber);
            _subscriberCount++;
            transportSubscriptionTask = _transportSubscriptionTask ??= _transport.SubscribeAsync(DispatchRecordAsync, ct);
        }

        try
        {
            await transportSubscriptionTask;
        }
        catch
        {
            lock (_gate)
            {
                RemoveSubscriberUnsafe(actorId, subscriber);

                if (ReferenceEquals(_transportSubscriptionTask, transportSubscriptionTask))
                    _transportSubscriptionTask = null;
            }

            throw;
        }

        await ReleaseTransportSubscriptionIfIdleAsync();
        return new SubscriptionLease(this, actorId, subscriber);
    }

    private async Task DispatchRecordAsync(MassTransitEnvelopeRecord record)
    {
        if (!string.Equals(record.StreamNamespace, _options.StreamNamespace, StringComparison.Ordinal) ||
            record.Payload is not { Length: > 0 })
        {
            return;
        }

        Subscriber[] subscribers;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(record.StreamId, out var actorSubscribers) ||
                actorSubscribers.Count == 0)
            {
                return;
            }

            subscribers = actorSubscribers.ToArray();
        }

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

    private async ValueTask UnsubscribeAsync(string actorId, Subscriber subscriber)
    {
        Task<IAsyncDisposable>? transportSubscriptionTask = null;

        lock (_gate)
        {
            if (!RemoveSubscriberUnsafe(actorId, subscriber))
                return;

            if (_subscriberCount == 0 && _transportSubscriptionTask != null)
            {
                if (_transportSubscriptionTask.IsCompletedSuccessfully)
                {
                    transportSubscriptionTask = _transportSubscriptionTask;
                }

                _transportSubscriptionTask = null;
            }
        }

        if (transportSubscriptionTask != null)
            await (await transportSubscriptionTask).DisposeAsync();
    }

    private async Task ReleaseTransportSubscriptionIfIdleAsync()
    {
        Task<IAsyncDisposable>? transportSubscriptionTask = null;

        lock (_gate)
        {
            if (_subscriberCount != 0 ||
                _transportSubscriptionTask is not { IsCompletedSuccessfully: true } currentTask)
            {
                return;
            }

            _transportSubscriptionTask = null;
            transportSubscriptionTask = currentTask;
        }

        if (transportSubscriptionTask != null)
            await (await transportSubscriptionTask).DisposeAsync();
    }

    private bool RemoveSubscriberUnsafe(string actorId, Subscriber subscriber)
    {
        if (!_subscribers.TryGetValue(actorId, out var actorSubscribers) ||
            !actorSubscribers.Remove(subscriber))
        {
            return false;
        }

        _subscriberCount--;
        if (actorSubscribers.Count == 0)
            _subscribers.Remove(actorId);

        return true;
    }

    private sealed class SubscriptionLease : IAsyncDisposable
    {
        private readonly MassTransitActorEventSubscriptionProvider _owner;
        private readonly string _actorId;
        private readonly Subscriber _subscriber;
        private int _disposed;

        public SubscriptionLease(
            MassTransitActorEventSubscriptionProvider owner,
            string actorId,
            Subscriber subscriber)
        {
            _owner = owner;
            _actorId = actorId;
            _subscriber = subscriber;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return ValueTask.CompletedTask;

            return new ValueTask(_owner.UnsubscribeAsync(_actorId, _subscriber).AsTask());
        }
    }

    private sealed class Subscriber
    {
        private readonly Func<EventEnvelope, Task> _dispatchAsync;

        private Subscriber(Func<EventEnvelope, Task> dispatchAsync)
        {
            _dispatchAsync = dispatchAsync;
        }

        public Task DispatchAsync(EventEnvelope envelope) => _dispatchAsync(envelope);

        public static Subscriber Create<TMessage>(Func<TMessage, Task> handler)
            where TMessage : class, IMessage, new()
        {
            if (typeof(TMessage) == typeof(EventEnvelope))
            {
                return new Subscriber(envelope => ((Func<EventEnvelope, Task>)(object)handler)(envelope));
            }

            var descriptor = new TMessage().Descriptor;
            return new Subscriber(envelope =>
            {
                if (envelope.Payload == null || !envelope.Payload.Is(descriptor))
                    return Task.CompletedTask;

                return handler(envelope.Payload.Unpack<TMessage>());
            });
        }
    }
}
