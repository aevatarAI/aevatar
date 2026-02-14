namespace Aevatar.Cqrs.Projections.Abstractions;

/// <summary>
/// Generic coordinator contract for projection pipeline lifecycle.
/// </summary>
public interface IProjectionCoordinator<in TContext, in TTopology>
{
    Task InitializeAsync(TContext context, CancellationToken ct = default);

    Task ProjectAsync(TContext context, EventEnvelope envelope, CancellationToken ct = default);

    Task CompleteAsync(TContext context, TTopology topology, CancellationToken ct = default);
}
