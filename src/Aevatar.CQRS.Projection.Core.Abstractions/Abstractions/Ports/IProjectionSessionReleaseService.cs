namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Release contract for one externally observable projection session lease.
/// </summary>
public interface IProjectionSessionReleaseService<TLease>
{
    Task ReleaseIfIdleAsync(
        TLease lease,
        CancellationToken ct = default);
}
