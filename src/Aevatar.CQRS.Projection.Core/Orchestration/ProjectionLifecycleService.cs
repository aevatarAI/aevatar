namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic projection lifecycle service that unifies coordinator and subscription registry orchestration.
/// </summary>
public sealed class ProjectionLifecycleService<TContext, TCompletion>
    : IProjectionLifecycleService<TContext, TCompletion>
    where TContext : IProjectionRunContext
{
    private readonly IProjectionCoordinator<TContext, TCompletion> _coordinator;
    private readonly IProjectionSubscriptionRegistry<TContext> _subscriptionRegistry;

    public ProjectionLifecycleService(
        IProjectionCoordinator<TContext, TCompletion> coordinator,
        IProjectionSubscriptionRegistry<TContext> subscriptionRegistry)
    {
        _coordinator = coordinator;
        _subscriptionRegistry = subscriptionRegistry;
    }

    public async Task StartAsync(TContext context, CancellationToken ct = default)
    {
        await _coordinator.InitializeAsync(context, ct);
        await _subscriptionRegistry.RegisterAsync(context, ct);
    }

    public Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _coordinator.ProjectAsync(context, envelope, ct);

    public Task<bool> WaitForCompletionAsync(string runId, TimeSpan timeout, CancellationToken ct = default) =>
        _subscriptionRegistry.WaitForCompletionAsync(runId, timeout, ct);

    public async Task CompleteAsync(TContext context, TCompletion completion, CancellationToken ct = default)
    {
        await _subscriptionRegistry.UnregisterAsync(context.RootActorId, context.RunId, ct);
        await _coordinator.CompleteAsync(context, completion, ct);
    }
}
