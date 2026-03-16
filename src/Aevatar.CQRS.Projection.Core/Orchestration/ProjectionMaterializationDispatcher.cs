namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Default durable materialization dispatcher.
/// </summary>
public sealed class ProjectionMaterializationDispatcher<TContext> : IProjectionMaterializationDispatcher<TContext>
    where TContext : IProjectionMaterializationContext
{
    private readonly IProjectionMaterializationCoordinator<TContext> _coordinator;

    public ProjectionMaterializationDispatcher(IProjectionMaterializationCoordinator<TContext> coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public Task DispatchAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default) =>
        _coordinator.ProjectAsync(context, envelope, ct);
}
