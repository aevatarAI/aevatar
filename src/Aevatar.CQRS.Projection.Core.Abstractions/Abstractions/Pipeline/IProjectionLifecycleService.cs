namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic projection lifecycle service.
/// </summary>
public interface IProjectionLifecycleService<TContext, in TRuntimeLease>
    where TContext : class, IProjectionSessionContext
    where TRuntimeLease : IProjectionRuntimeLease, IProjectionContextRuntimeLease<TContext>
{
    Task StartAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);

    Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);

    Task StopAsync(TRuntimeLease runtimeLease, CancellationToken ct = default);
}
