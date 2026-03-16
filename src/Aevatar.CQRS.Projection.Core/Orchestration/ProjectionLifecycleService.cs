namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic projection lifecycle service that unifies coordinator and subscription registry orchestration.
/// </summary>
public sealed class ProjectionLifecycleService<TContext, TRuntimeLease>
    : IProjectionLifecycleService<TContext, TRuntimeLease>
    where TContext : class, IProjectionSessionContext
    where TRuntimeLease : IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
{
    private readonly IProjectionDispatcher<TContext> _dispatcher;
    private readonly IProjectionSubscriptionRegistry<TContext, TRuntimeLease> _subscriptionRegistry;

    public ProjectionLifecycleService(
        IProjectionDispatcher<TContext> dispatcher,
        IProjectionSubscriptionRegistry<TContext, TRuntimeLease> subscriptionRegistry)
    {
        _dispatcher = dispatcher;
        _subscriptionRegistry = subscriptionRegistry;
    }

    public Task StartAsync(TRuntimeLease runtimeLease, CancellationToken ct = default) =>
        _subscriptionRegistry.RegisterAsync(runtimeLease, ct);

    public Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _dispatcher.DispatchAsync(context, envelope, ct);

    public Task StopAsync(TRuntimeLease runtimeLease, CancellationToken ct = default) =>
        _subscriptionRegistry.UnregisterAsync(runtimeLease, ct);
}
