namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic run-scoped projection lifecycle service.
/// </summary>
public interface IProjectionLifecycleService<in TContext, in TCompletion>
    where TContext : IProjectionRunContext
{
    Task StartAsync(TContext context, CancellationToken ct = default);

    Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);

    Task<bool> WaitForCompletionAsync(string runId, TimeSpan timeout, CancellationToken ct = default);

    Task CompleteAsync(TContext context, TCompletion completion, CancellationToken ct = default);
}
