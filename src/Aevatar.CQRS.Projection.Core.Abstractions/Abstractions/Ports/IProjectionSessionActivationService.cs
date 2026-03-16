namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Activation contract for one externally observable projection session.
/// </summary>
public interface IProjectionSessionActivationService<TLease>
{
    Task<TLease> EnsureAsync(
        ProjectionSessionStartRequest request,
        CancellationToken ct = default);
}
