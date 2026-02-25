namespace Aevatar.CQRS.Projection.Core.Abstractions;

/// <summary>
/// Generic activation contract for projection runtime lease acquisition.
/// </summary>
public interface IProjectionPortActivationService<TLease>
{
    Task<TLease> EnsureAsync(
        string rootEntityId,
        string projectionName,
        string input,
        string commandId,
        CancellationToken ct = default);
}
