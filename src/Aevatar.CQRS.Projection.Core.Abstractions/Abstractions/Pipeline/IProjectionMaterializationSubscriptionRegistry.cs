namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Actor-level durable materialization subscription lifecycle.
/// </summary>
public interface IProjectionMaterializationSubscriptionRegistry<TContext, in TRuntimeLease>
    where TContext : class, IProjectionMaterializationContext
    where TRuntimeLease : IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
{
    Task RegisterAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);

    Task UnregisterAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);
}
