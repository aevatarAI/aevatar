namespace Aevatar.CQRS.Projection.Stores.Abstractions;

/// <summary>
/// A read model whose writer is selected by an actor-owned, monotonic route epoch. At the same
/// authoritative source version a write may take over an existing document only under a
/// strictly higher route epoch (the epoch-fenced same-version takeover a writer cutover relies
/// on); equal epochs must be byte-identical (duplicate) or conflict, and a lower epoch is stale.
/// The fence never applies across source versions: a higher source version always wins, so a
/// writer that does not know the route (epoch 0) can still take a document forward.
/// </summary>
public interface IProjectionRouteFencedReadModel : IProjectionReadModel
{
    long RouteEpoch { get; }
}
