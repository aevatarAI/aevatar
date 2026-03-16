namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic registry for actor-level projection subscription lifecycle.
/// </summary>
public interface IProjectionSubscriptionRegistry<TContext, in TRuntimeLease>
    where TContext : class, IProjectionSessionContext
    where TRuntimeLease : IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
{
    Task RegisterAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);

    Task UnregisterAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);
}
