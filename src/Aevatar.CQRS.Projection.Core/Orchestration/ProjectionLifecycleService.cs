namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic projection lifecycle service that unifies coordinator and subscription registry orchestration.
/// </summary>
public sealed class ProjectionLifecycleService<TContext, TCompletion>
    : IProjectionLifecycleService<TContext, TCompletion>
    where TContext : IProjectionContext
{
    private readonly IProjectionCoordinator<TContext, TCompletion> _coordinator;
    private readonly IProjectionDispatcher<TContext> _dispatcher;
    private readonly IProjectionSubscriptionRegistry<TContext> _subscriptionRegistry;

    public ProjectionLifecycleService(
        IProjectionCoordinator<TContext, TCompletion> coordinator,
        IProjectionDispatcher<TContext> dispatcher,
        IProjectionSubscriptionRegistry<TContext> subscriptionRegistry)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
        _subscriptionRegistry = subscriptionRegistry;
    }

    public async Task StartAsync(TContext context, CancellationToken ct = default)
    {
        await _coordinator.InitializeAsync(context, ct);
        await _subscriptionRegistry.RegisterAsync(context, ct);
    }

    public Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _dispatcher.DispatchAsync(context, envelope, ct);

    public Task StopAsync(TContext context, CancellationToken ct = default) =>
        _subscriptionRegistry.UnregisterAsync(context, ct);

    public async Task CompleteAsync(TContext context, TCompletion completion, CancellationToken ct = default)
    {
        await _subscriptionRegistry.UnregisterAsync(context, ct);
        await _coordinator.CompleteAsync(context, completion, ct);
    }
}
