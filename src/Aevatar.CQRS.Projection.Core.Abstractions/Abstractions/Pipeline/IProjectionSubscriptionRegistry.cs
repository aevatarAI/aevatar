namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic registry for actor-level projection subscription lifecycle.
/// </summary>
public interface IProjectionSubscriptionRegistry<TContext>
    where TContext : IProjectionContext
{
    Task RegisterAsync(TContext context, CancellationToken ct = default);

    Task UnregisterAsync(TContext context, CancellationToken ct = default);
}
