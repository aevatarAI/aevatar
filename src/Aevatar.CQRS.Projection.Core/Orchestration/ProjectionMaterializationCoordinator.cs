namespace Aevatar.CQRS.Projection.Core.Orchestration;

/// <summary>
/// Durable materialization coordinator that executes materializers in registration order.
/// </summary>
public sealed class ProjectionMaterializationCoordinator<TContext>
    : IProjectionMaterializationCoordinator<TContext>
    where TContext : IProjectionMaterializationContext
{
    private readonly IReadOnlyList<IProjectionMaterializer<TContext>> _materializers;

    public ProjectionMaterializationCoordinator(IEnumerable<IProjectionMaterializer<TContext>> materializers) =>
        _materializers = materializers.ToList();

    public async Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default)
    {
        List<ProjectionDispatchFailure>? failures = null;
        for (var index = 0; index < _materializers.Count; index++)
        {
            var materializer = _materializers[index];
            try
            {
                await materializer.ProjectAsync(context, envelope, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(new ProjectionDispatchFailure(materializer.GetType().Name, index + 1, ex));
            }
        }

        if (failures is { Count: > 0 })
            throw new ProjectionDispatchAggregateException(failures);
    }
}
