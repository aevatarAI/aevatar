namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Lifecycle for durable materialization subscriptions.
/// </summary>
public interface IProjectionMaterializationLifecycleService<TContext, in TRuntimeLease>
    where TContext : class, IProjectionMaterializationContext
    where TRuntimeLease : IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
{
    Task StartAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);

    Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);

    Task StopAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);
}
