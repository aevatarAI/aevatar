using Google.Protobuf;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Aevatar.CQRS.Projection.Core.Streaming;

/// <summary>
/// Default generic actor-stream hub implementation.
/// Keeps one underlying stream subscription per actor and fan-outs to registered handlers.
/// </summary>
public sealed class ActorStreamSubscriptionHub<TMessage> : IActorStreamSubscriptionHub<TMessage>, IAsyncDisposable
    where TMessage : class, IMessage, new()
{
    private readonly IStreamProvider _streams;
    private readonly ILogger<ActorStreamSubscriptionHub<TMessage>>? _logger;
    private readonly ConcurrentDictionary<string, ActorSubscriptionState> _statesByActor = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _disposed;

    public ActorStreamSubscriptionHub(
        IStreamProvider streams,
        ILogger<ActorStreamSubscriptionHub<TMessage>>? logger = null)
    {
        _streams = streams;
        _logger = logger;
    }

    public async Task RegisterAsync(
        string actorId,
        string key,
        Func<TMessage, ValueTask> handler,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Handler key is required.", nameof(key));
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfDisposed();
            if (!_statesByActor.TryGetValue(actorId, out var state))
            {
                var stream = _streams.GetStream(actorId);
                var streamSubscription = await stream.SubscribeAsync<TMessage>(
                    message => DispatchAsync(actorId, message),
                    ct);

                state = new ActorSubscriptionState(streamSubscription);
                _statesByActor[actorId] = state;
            }

            if (!state.Handlers.TryAdd(key, handler))
                throw new InvalidOperationException($"Handler '{key}' is already registered for actor '{actorId}'.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnregisterAsync(
        string actorId,
        string key,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(key))
            return;

        IAsyncDisposable? streamSubscriptionToDispose = null;

        await _gate.WaitAsync(ct);
        try
        {
            if (!_statesByActor.TryGetValue(actorId, out var state))
                return;

            state.Handlers.TryRemove(key, out _);
            if (!state.Handlers.IsEmpty)
                return;

            _statesByActor.TryRemove(actorId, out _);
            streamSubscriptionToDispose = state.StreamSubscription;
        }
        finally
        {
            _gate.Release();
        }

        if (streamSubscriptionToDispose != null)
            await streamSubscriptionToDispose.DisposeAsync();
    }

    private async Task DispatchAsync(string actorId, TMessage message)
    {
        if (!_statesByActor.TryGetValue(actorId, out var state))
            return;

        var handlers = state.Handlers.Values.ToArray();
        foreach (var handler in handlers)
        {
            try
            {
                await handler(message);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Actor stream dispatch failed for actor {ActorId}.", actorId);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        List<IAsyncDisposable> streamSubscriptions;
        await _gate.WaitAsync();
        try
        {
            streamSubscriptions = _statesByActor.Values
                .Select(x => x.StreamSubscription)
                .ToList();
            _statesByActor.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }

        foreach (var streamSubscription in streamSubscriptions)
        {
            try
            {
                await streamSubscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to dispose actor stream subscription.");
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
    }

    private sealed class ActorSubscriptionState
    {
        public ActorSubscriptionState(IAsyncDisposable streamSubscription) =>
            StreamSubscription = streamSubscription;

        public IAsyncDisposable StreamSubscription { get; }
        public ConcurrentDictionary<string, Func<TMessage, ValueTask>> Handlers { get; } =
            new(StringComparer.Ordinal);
    }
}
