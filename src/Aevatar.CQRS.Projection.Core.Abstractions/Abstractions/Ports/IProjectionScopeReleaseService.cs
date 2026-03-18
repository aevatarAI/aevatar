namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Unified release contract for projection scope leases (session or materialization).
/// </summary>
public interface IProjectionScopeReleaseService<in TLease>
    where TLease : class, IProjectionRuntimeLease
{
    Task ReleaseIfIdleAsync(
        TLease lease,
        CancellationToken ct = default);
}
