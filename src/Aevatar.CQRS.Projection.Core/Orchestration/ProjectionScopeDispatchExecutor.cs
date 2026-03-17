namespace Aevatar.CQRS.Projection.Core.Orchestration;

internal static class ProjectionScopeDispatchExecutor
{
    public static async Task ExecuteMaterializersAsync<TContext>(
        IEnumerable<IProjectionMaterializer<TContext>> materializers,
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
        where TContext : class, IProjectionMaterializationContext
    {
        ArgumentNullException.ThrowIfNull(materializers);
        await ExecuteCoreAsync(
            materializers,
            context,
            envelope,
            static (materializer, innerContext, innerEnvelope, innerCt) =>
                materializer.ProjectAsync(innerContext, innerEnvelope, innerCt),
            ct);
    }

    public static async Task ExecuteProjectorsAsync<TContext>(
        IEnumerable<IProjectionProjector<TContext>> projectors,
        TContext context,
        EventEnvelope envelope,
        CancellationToken ct = default)
        where TContext : class, IProjectionSessionContext
    {
        ArgumentNullException.ThrowIfNull(projectors);
        await ExecuteCoreAsync(
            projectors,
            context,
            envelope,
            static (projector, innerContext, innerEnvelope, innerCt) =>
                projector.ProjectAsync(innerContext, innerEnvelope, innerCt),
            ct);
    }

    private static async Task ExecuteCoreAsync<THandler, TContext>(
        IEnumerable<THandler> handlers,
        TContext context,
        EventEnvelope envelope,
        Func<THandler, TContext, EventEnvelope, CancellationToken, ValueTask> invokeAsync,
        CancellationToken ct)
        where TContext : class, IProjectionMaterializationContext
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(invokeAsync);

        List<ProjectionDispatchFailure>? failures = null;
        var index = 0;
        foreach (var handler in handlers)
        {
            index++;
            try
            {
                await invokeAsync(handler, context, envelope, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(new ProjectionDispatchFailure(handler?.GetType().Name ?? "unknown", index, ex));
            }
        }

        if (failures is { Count: > 0 })
            throw new ProjectionDispatchAggregateException(failures);
    }
}
