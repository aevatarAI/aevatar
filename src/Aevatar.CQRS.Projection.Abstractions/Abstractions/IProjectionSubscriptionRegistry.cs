namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic registry for actor-level projection subscription lifecycle.
/// </summary>
public interface IProjectionSubscriptionRegistry<in TContext>
    where TContext : IProjectionContext
{
    Task RegisterAsync(TContext context, CancellationToken ct = default);

    Task UnregisterAsync(string actorId, CancellationToken ct = default);
}
