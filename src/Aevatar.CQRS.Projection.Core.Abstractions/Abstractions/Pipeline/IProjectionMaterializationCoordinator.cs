namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Coordinator contract for durable materialization dispatch.
/// </summary>
public interface IProjectionMaterializationCoordinator<in TContext>
    where TContext : IProjectionMaterializationContext
{
    Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
