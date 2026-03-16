namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Generic projector coordinator that executes projectors in registration order.
/// </summary>
public class ProjectionCoordinator<TContext> : IProjectionCoordinator<TContext>
    where TContext : IProjectionSessionContext
{
    private readonly IReadOnlyList<IProjectionProjector<TContext>> _projectors;

    public ProjectionCoordinator(IEnumerable<IProjectionProjector<TContext>> projectors) =>
        _projectors = projectors.ToList();

    public async Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        List<ProjectionDispatchFailure>? failures = null;
        for (var index = 0; index < _projectors.Count; index++)
        {
            var projector = _projectors[index];
            try
            {
                await projector.ProjectAsync(context, envelope, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(new ProjectionDispatchFailure(projector.GetType().Name, index + 1, ex));
            }
        }

        if (failures is { Count: > 0 })
            throw new ProjectionDispatchAggregateException(failures);
    }
}
