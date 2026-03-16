namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic coordinator contract for projection pipeline dispatch.
/// </summary>
public interface IProjectionCoordinator<in TContext>
    where TContext : IProjectionSessionContext
{
    Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
