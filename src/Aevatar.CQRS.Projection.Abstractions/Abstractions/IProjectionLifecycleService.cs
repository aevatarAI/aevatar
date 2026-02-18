namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic projection lifecycle service.
/// </summary>
public interface IProjectionLifecycleService<in TContext, in TCompletion>
    where TContext : IProjectionContext
{
    Task StartAsync(TContext context, CancellationToken ct = default);

    Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);

    Task CompleteAsync(TContext context, TCompletion completion, CancellationToken ct = default);
}
