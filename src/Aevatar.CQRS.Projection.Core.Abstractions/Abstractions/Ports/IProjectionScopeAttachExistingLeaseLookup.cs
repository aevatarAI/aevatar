namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Typed lookup contract for attaching to an already-existing projection scope lease.
/// </summary>
public interface IProjectionScopeAttachExistingLeaseLookup<TLease>
    where TLease : class, IProjectionRuntimeLease
{
    Task<TLease?> TryGetAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default);
}
