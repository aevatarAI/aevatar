namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Release contract for one durable materialization scope lease.
/// </summary>
public interface IProjectionMaterializationReleaseService<TLease>
{
    Task ReleaseIfIdleAsync(
        TLease lease,
        CancellationToken ct = default);
}
