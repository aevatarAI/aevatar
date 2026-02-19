using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Actor-level projection registry built on top of shared actor stream subscriptions.
/// </summary>
public sealed class ProjectionSubscriptionRegistry<TContext>
    : IProjectionSubscriptionRegistry<TContext>, IAsyncDisposable
    where TContext : IProjectionContext
{
    private readonly IProjectionDispatcher<TContext> _dispatcher;
    private readonly IActorStreamSubscriptionHub<EventEnvelope> _subscriptionHub;
    private readonly ILogger<ProjectionSubscriptionRegistry<TContext>>? _logger;
    private readonly ConcurrentDictionary<string, ActiveProjectionState> _activeStatesByActorId = new(StringComparer.Ordinal);
    private int _disposed;

    public ProjectionSubscriptionRegistry(
        IProjectionDispatcher<TContext> dispatcher,
        IActorStreamSubscriptionHub<EventEnvelope> subscriptionHub,
        ILogger<ProjectionSubscriptionRegistry<TContext>>? logger = null)
    {
        _dispatcher = dispatcher;
        _subscriptionHub = subscriptionHub;
        _logger = logger;
    }

    public async Task RegisterAsync(TContext context, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);

        var actorId = context.RootActorId;
        var envelopeSubscription = await _subscriptionHub.RegisterAsync(
            actorId,
            envelope => DispatchAsync(actorId, envelope),
            ct);

        var state = new ActiveProjectionState(context, envelopeSubscription);
        if (!_activeStatesByActorId.TryAdd(actorId, state))
        {
            await envelopeSubscription.DisposeAsync();
            throw new InvalidOperationException($"Projection already registered for actor '{actorId}'.");
        }

        _logger?.LogDebug(
            "Registered projection {ProjectionId} for actor {ActorId}.",
            context.ProjectionId,
            actorId);
    }

    public async Task UnregisterAsync(string actorId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_activeStatesByActorId.TryRemove(actorId, out var state))
            return;

        state.CancelDispatch();
        await state.Subscription.DisposeAsync();
        state.Dispose();
    }

    private async ValueTask DispatchAsync(string actorId, EventEnvelope envelope)
    {
        if (!_activeStatesByActorId.TryGetValue(actorId, out var state))
            return;

        try
        {
            await _dispatcher.DispatchAsync(state.Context, envelope, state.DispatchToken);
        }
        catch (OperationCanceledException) when (state.DispatchToken.IsCancellationRequested)
        {
            // Ignore cancellation during unregistration/disposal.
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "Projection dispatch failed for projection {ProjectionId} on actor {ActorId}.",
                state.Context.ProjectionId,
                actorId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        var states = _activeStatesByActorId.Values.ToList();
        _activeStatesByActorId.Clear();

        foreach (var state in states)
        {
            state.CancelDispatch();
            try
            {
                await state.Subscription.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to dispose projection subscription.");
            }
            finally
            {
                state.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
    }

    private sealed class ActiveProjectionState
    {
        private readonly CancellationTokenSource _dispatchCancellation = new();

        public ActiveProjectionState(
            TContext context,
            IAsyncDisposable subscription)
        {
            Context = context;
            Subscription = subscription;
        }

        public TContext Context { get; }
        public IAsyncDisposable Subscription { get; }
        public CancellationToken DispatchToken => _dispatchCancellation.Token;

        public void CancelDispatch()
        {
            if (_dispatchCancellation.IsCancellationRequested)
                return;

            _dispatchCancellation.Cancel();
        }

        public void Dispose() => _dispatchCancellation.Dispose();
    }
}
