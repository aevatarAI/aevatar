namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Default projection dispatcher backed by projection coordinator.
/// </summary>
public sealed class ProjectionDispatcher<TContext> : IProjectionDispatcher<TContext>
    where TContext : IProjectionSessionContext
{
    private readonly IProjectionCoordinator<TContext> _coordinator;

    public ProjectionDispatcher(IProjectionCoordinator<TContext> coordinator)
    {
        _coordinator = coordinator;
    }

    public Task DispatchAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _coordinator.ProjectAsync(context, envelope, ct);
}
