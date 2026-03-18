namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Unified activation contract for projection scopes (session or materialization).
/// </summary>
public interface IProjectionScopeActivationService<TLease>
    where TLease : class, IProjectionRuntimeLease
{
    Task<TLease> EnsureAsync(
        ProjectionScopeStartRequest request,
        CancellationToken ct = default);
}
