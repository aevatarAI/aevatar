namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Default projection dispatcher backed by projection coordinator.
/// </summary>
public sealed class ProjectionDispatcher<TContext, TTopology> : IProjectionDispatcher<TContext>
{
    private readonly IProjectionCoordinator<TContext, TTopology> _coordinator;

    public ProjectionDispatcher(IProjectionCoordinator<TContext, TTopology> coordinator)
    {
        _coordinator = coordinator;
    }

    public Task DispatchAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _coordinator.ProjectAsync(context, envelope, ct);
}
