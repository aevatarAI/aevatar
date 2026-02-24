namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Generic release contract for projection runtime lease lifecycle.
/// </summary>
public interface IProjectionPortReleaseService<TLease>
{
    Task ReleaseIfIdleAsync(
        TLease lease,
        CancellationToken ct = default);
}
