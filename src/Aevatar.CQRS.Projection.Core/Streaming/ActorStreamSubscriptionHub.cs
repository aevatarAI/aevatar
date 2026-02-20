using Google.Protobuf;
using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Streaming;

/// <summary>
/// Default generic actor-stream hub implementation.
/// Creates direct actor stream subscriptions and returns disposable leases.
/// </summary>
public sealed class ActorStreamSubscriptionHub<TMessage> : IActorStreamSubscriptionHub<TMessage>, IAsyncDisposable
    where TMessage : class, IMessage, new()
{
    private readonly IStreamProvider _streams;
    private readonly ILogger<ActorStreamSubscriptionHub<TMessage>>? _logger;
    private int _disposed;

    public ActorStreamSubscriptionHub(
        IStreamProvider streams,
        ILogger<ActorStreamSubscriptionHub<TMessage>>? logger = null)
    {
        _streams = streams;
        _logger = logger;
    }

    public async Task<IActorStreamSubscriptionLease> SubscribeAsync(
        string actorId,
        Func<TMessage, ValueTask> handler,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));
        ArgumentNullException.ThrowIfNull(handler);
        ThrowIfDisposed();

        var stream = _streams.GetStream(actorId);
        var streamSubscription = await stream.SubscribeAsync<TMessage>(
            message => DispatchAsync(actorId, handler, message),
            ct);

        return new ActorStreamSubscriptionLease(actorId, streamSubscription, _logger);
    }

    private async Task DispatchAsync(string actorId, Func<TMessage, ValueTask> handler, TMessage message)
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

    public ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return ValueTask.CompletedTask;

        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
    }

    private sealed class ActorStreamSubscriptionLease : IActorStreamSubscriptionLease
    {
        private readonly IAsyncDisposable _subscription;
        private readonly ILogger<ActorStreamSubscriptionHub<TMessage>>? _logger;
        private int _disposed;

        public ActorStreamSubscriptionLease(
            string actorId,
            IAsyncDisposable subscription,
            ILogger<ActorStreamSubscriptionHub<TMessage>>? logger)
        {
            ActorId = actorId;
            _subscription = subscription;
            _logger = logger;
        }

        public string ActorId { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try
            {
                await _subscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to dispose actor stream subscription lease for actor {ActorId}.", ActorId);
            }
        }
    }
}
