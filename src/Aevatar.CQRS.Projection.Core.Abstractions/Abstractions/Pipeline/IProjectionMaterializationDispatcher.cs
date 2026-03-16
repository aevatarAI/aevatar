namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Durable materialization dispatch entry for one committed envelope.
/// </summary>
public interface IProjectionMaterializationDispatcher<in TContext>
    where TContext : IProjectionMaterializationContext
{
    Task DispatchAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
