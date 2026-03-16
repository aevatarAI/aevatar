namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Durable committed-observation materializer contract.
/// </summary>
public interface IProjectionMaterializer<in TContext>
    where TContext : IProjectionMaterializationContext
{
    ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
