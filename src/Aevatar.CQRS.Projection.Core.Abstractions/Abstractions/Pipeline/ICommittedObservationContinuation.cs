namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Durable committed-observation continuation contract.
/// Implementations observe committed facts from the projection pipeline and may
/// advance business protocols through explicit command ports. They must not
/// materialize read models or artifacts.
/// </summary>
public interface ICommittedObservationContinuation<in TContext>
    where TContext : IProjectionMaterializationContext
{
    ValueTask ContinueAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
