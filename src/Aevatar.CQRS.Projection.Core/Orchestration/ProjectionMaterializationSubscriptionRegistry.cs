using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Actor-level durable materialization registry built on shared actor stream subscriptions.
/// </summary>
public sealed class ProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease>
    : IProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease>, IAsyncDisposable, IDisposable
    where TContext : class, IProjectionMaterializationContext
    where TRuntimeLease : class, IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>, IProjectionStreamSubscriptionRuntimeLease
{
    private readonly IProjectionMaterializationDispatcher<TContext> _dispatcher;
    private readonly IActorStreamSubscriptionHub<EventEnvelope> _subscriptionHub;
    private readonly IProjectionDispatchFailureReporter<TContext>? _dispatchFailureReporter;
    private readonly ILogger<ProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease>>? _logger;
    private int _disposed;

    public ProjectionMaterializationSubscriptionRegistry(
        IProjectionMaterializationDispatcher<TContext> dispatcher,
        IActorStreamSubscriptionHub<EventEnvelope> subscriptionHub,
        IProjectionDispatchFailureReporter<TContext>? dispatchFailureReporter = null,
        ILogger<ProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease>>? logger = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _subscriptionHub = subscriptionHub ?? throw new ArgumentNullException(nameof(subscriptionHub));
        _dispatchFailureReporter = dispatchFailureReporter;
        _logger = logger;
    }

    public async Task RegisterAsync(TRuntimeLease runtimeLease, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(runtimeLease);

        var context = runtimeLease.Context;
        if (runtimeLease.ActorStreamSubscriptionLease != null)
            throw new InvalidOperationException(
                $"Materialization '{context.ProjectionKind}' for actor '{context.RootActorId}' is already registered.");

        var actorId = context.RootActorId;
        var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var lease = await _subscriptionHub.SubscribeAsync(
                actorId,
                envelope => DispatchAsync(actorId, context, envelope, dispatchCts.Token),
                ct);
            runtimeLease.ActorStreamSubscriptionLease = new ProjectionStreamSubscriptionLease(actorId, lease, dispatchCts);
        }
        catch
        {
            dispatchCts.Dispose();
            throw;
        }

        _logger?.LogDebug(
            "Registered durable materialization {ProjectionKind} for actor {ActorId}.",
            context.ProjectionKind,
            actorId);
    }

    public async Task UnregisterAsync(TRuntimeLease runtimeLease, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(runtimeLease);
        ThrowIfDisposed();

        var context = runtimeLease.Context;
        if (string.IsNullOrWhiteSpace(context.RootActorId) ||
            string.IsNullOrWhiteSpace(context.ProjectionKind))
            return;

        var lease = runtimeLease.ActorStreamSubscriptionLease;
        if (lease == null)
            return;

        runtimeLease.ActorStreamSubscriptionLease = null;
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

            if (!ProjectionDispatchRouteFilter.ShouldDispatch(envelope))
                return;

            await _dispatcher.DispatchAsync(context, envelope, dispatchToken);
        }
        catch (OperationCanceledException) when (dispatchToken.IsCancellationRequested)
        {
            _logger?.LogDebug(
                "Durable materialization dispatch cancelled for projection {ProjectionKind} on actor {ActorId}.",
                context.ProjectionKind,
                actorId);
        }
        catch (Exception ex)
        {
            if (_dispatchFailureReporter != null)
            {
                try
                {
                    await _dispatchFailureReporter.ReportAsync(context, envelope, ex, CancellationToken.None);
                }
                catch (Exception reportEx)
                {
                    _logger?.LogError(
                        reportEx,
                        "Durable materialization failure reporter failed for projection {ProjectionKind} on actor {ActorId}.",
                        context.ProjectionKind,
                        actorId);
                }
            }

            _logger?.LogWarning(
                ex,
                "Durable materialization dispatch failed for projection {ProjectionKind} on actor {ActorId}.",
                context.ProjectionKind,
                actorId);
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
            return;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
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
