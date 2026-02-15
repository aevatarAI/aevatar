namespace Aevatar.CQRS.Projection.Contracts;

/// <summary>
/// Generic registry for run-level projection subscription lifecycle.
/// </summary>
public interface IProjectionSubscriptionRegistry<in TContext>
    where TContext : IProjectionRunContext
{
    Task RegisterAsync(TContext context, CancellationToken ct = default);

    Task UnregisterAsync(string actorId, string runId, CancellationToken ct = default);

    Task<bool> WaitForCompletionAsync(string runId, TimeSpan timeout, CancellationToken ct = default);
}
