using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Actor-level projection registry built on top of shared actor stream subscriptions.
/// </summary>
public sealed class ProjectionSubscriptionRegistry<TContext>
    : IProjectionSubscriptionRegistry<TContext>, IAsyncDisposable
    where TContext : IProjectionContext, IProjectionStreamSubscriptionContext
{
    private readonly IProjectionDispatcher<TContext> _dispatcher;
    private readonly IActorStreamSubscriptionHub<EventEnvelope> _subscriptionHub;
    private readonly IProjectionDispatchFailureReporter<TContext>? _dispatchFailureReporter;
    private readonly ILogger<ProjectionSubscriptionRegistry<TContext>>? _logger;
    private int _disposed;

    public ProjectionSubscriptionRegistry(
        IProjectionDispatcher<TContext> dispatcher,
        IActorStreamSubscriptionHub<EventEnvelope> subscriptionHub,
        IProjectionDispatchFailureReporter<TContext>? dispatchFailureReporter = null,
        ILogger<ProjectionSubscriptionRegistry<TContext>>? logger = null)
    {
        _dispatcher = dispatcher;
        _subscriptionHub = subscriptionHub;
        _dispatchFailureReporter = dispatchFailureReporter;
        _logger = logger;
    }

    public async Task RegisterAsync(TContext context, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(context);
        if (context.StreamSubscriptionLease != null)
            throw new InvalidOperationException(
                $"Projection '{context.ProjectionId}' for actor '{context.RootActorId}' is already registered.");

        var actorId = context.RootActorId;
        var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var lease = await _subscriptionHub.SubscribeAsync(
                actorId,
                envelope => DispatchAsync(actorId, context, envelope, dispatchCts.Token),
                ct);
            context.StreamSubscriptionLease = new ProjectionStreamSubscriptionLease(actorId, lease, dispatchCts);
        }
        catch
        {
            dispatchCts.Dispose();
            throw;
        }

        _logger?.LogDebug(
            "Registered projection {ProjectionId} for actor {ActorId}.",
            context.ProjectionId,
            actorId);
    }

    public async Task UnregisterAsync(TContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(context.RootActorId) || string.IsNullOrWhiteSpace(context.ProjectionId))
            return;

        var lease = context.StreamSubscriptionLease;
        if (lease == null)
            return;

        context.StreamSubscriptionLease = null;
        await lease.DisposeAsync();
    }

    private async ValueTask DispatchAsync(
        string actorId,
        TContext context,
        EventEnvelope envelope,
        CancellationToken dispatchToken)
    {
        try
        {
            if (dispatchToken.IsCancellationRequested)
                return;

            await _dispatcher.DispatchAsync(context, envelope, dispatchToken);
        }
        catch (OperationCanceledException) when (dispatchToken.IsCancellationRequested)
        {
            _logger?.LogDebug(
                "Projection dispatch cancelled for projection {ProjectionId} on actor {ActorId}.",
                context.ProjectionId,
                actorId);
        }
        catch (Exception ex)
        {
            if (_dispatchFailureReporter != null)
            {
                try
                {
                    await _dispatchFailureReporter.ReportAsync(context, envelope, ex, dispatchToken);
                }
                catch (Exception reportEx)
                {
                    _logger?.LogError(
                        reportEx,
                        "Projection dispatch failure reporter failed for projection {ProjectionId} on actor {ActorId}.",
                        context.ProjectionId,
                        actorId);
                }
            }

            _logger?.LogWarning(
                ex,
                "Projection dispatch failed for projection {ProjectionId} on actor {ActorId}.",
                context.ProjectionId,
                actorId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;

        await Task.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(GetType().Name);
    }

    private sealed class ProjectionStreamSubscriptionLease : IActorStreamSubscriptionLease
    {
        private readonly IActorStreamSubscriptionLease _innerLease;
        private readonly CancellationTokenSource _dispatchCts;
        private int _disposed;

        public ProjectionStreamSubscriptionLease(
            string actorId,
            IActorStreamSubscriptionLease innerLease,
            CancellationTokenSource dispatchCts)
        {
            ActorId = actorId;
            _innerLease = innerLease;
            _dispatchCts = dispatchCts;
        }

        public string ActorId { get; }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            try
            {
                _dispatchCts.Cancel();
            }
            finally
            {
                _dispatchCts.Dispose();
            }

            await _innerLease.DisposeAsync();
        }
    }
}
