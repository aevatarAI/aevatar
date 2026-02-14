namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Generic projector contract for applying envelopes to one projection context.
/// </summary>
public interface IProjectionProjector<in TContext, in TTopology>
{
    int Order { get; }

    ValueTask InitializeAsync(TContext context, CancellationToken ct = default);

    ValueTask ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);

    ValueTask CompleteAsync(TContext context, TTopology topology, CancellationToken ct = default);
}
