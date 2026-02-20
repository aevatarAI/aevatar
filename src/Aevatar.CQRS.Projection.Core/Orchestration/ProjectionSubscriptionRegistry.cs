using Microsoft.Extensions.Logging;

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
        await _subscriptionHub.RegisterAsync(
            actorId,
            context.ProjectionId,
            envelope => DispatchAsync(actorId, context, envelope),
            ct);

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

        await _subscriptionHub.UnregisterAsync(context.RootActorId, context.ProjectionId, ct);
    }

    private async ValueTask DispatchAsync(string actorId, TContext context, EventEnvelope envelope)
    {
        try
        {
            await _dispatcher.DispatchAsync(context, envelope, CancellationToken.None);
        }
        catch (Exception ex)
        {
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
}
