namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic projector contract for applying envelopes to one projection session.
/// </summary>
public interface IProjectionProjector<in TContext>
    where TContext : IProjectionSessionContext
{
    ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);
}
