namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Lifecycle service for durable materialization subscriptions.
/// </summary>
public sealed class ProjectionMaterializationLifecycleService<TContext, TRuntimeLease>
    : IProjectionMaterializationLifecycleService<TContext, TRuntimeLease>
    where TContext : class, IProjectionMaterializationContext
    where TRuntimeLease : IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
{
    private readonly IProjectionMaterializationDispatcher<TContext> _dispatcher;
    private readonly IProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease> _subscriptionRegistry;

    public ProjectionMaterializationLifecycleService(
        IProjectionMaterializationDispatcher<TContext> dispatcher,
        IProjectionMaterializationSubscriptionRegistry<TContext, TRuntimeLease> subscriptionRegistry)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _subscriptionRegistry = subscriptionRegistry ?? throw new ArgumentNullException(nameof(subscriptionRegistry));
    }

    public Task StartAsync(TRuntimeLease runtimeLease, CancellationToken ct = default) =>
        _subscriptionRegistry.RegisterAsync(runtimeLease, ct);

    public Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _dispatcher.DispatchAsync(context, envelope, ct);

    public Task StopAsync(TRuntimeLease runtimeLease, CancellationToken ct = default) =>
        _subscriptionRegistry.UnregisterAsync(runtimeLease, ct);
}
