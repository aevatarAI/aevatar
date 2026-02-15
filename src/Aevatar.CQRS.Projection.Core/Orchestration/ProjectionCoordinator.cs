namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic projector coordinator that executes an ordered projector pipeline.
/// </summary>
public class ProjectionCoordinator<TContext, TTopology> : IProjectionCoordinator<TContext, TTopology>
{
    private readonly IReadOnlyList<IProjectionProjector<TContext, TTopology>> _projectors;

    public ProjectionCoordinator(IEnumerable<IProjectionProjector<TContext, TTopology>> projectors) =>
        _projectors = projectors.OrderBy(x => x.Order).ToList();

    public async Task InitializeAsync(TContext context, CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.InitializeAsync(context, ct);
    }

    public async Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.ProjectAsync(context, envelope, ct);
    }

    public async Task CompleteAsync(TContext context, TTopology topology, CancellationToken ct = default)
    {
        foreach (var projector in _projectors)
            await projector.CompleteAsync(context, topology, ct);
    }
}
