namespace Aevatar.CQRS.Projection.Abstractions;

/// <summary>
/// Coordinates projection ownership arbitration by scope/session key.
/// </summary>
public interface IProjectionOwnershipCoordinator
{
    Task AcquireAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default);

    Task<bool> HasActiveLeaseAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        string scopeId,
        string sessionId,
        CancellationToken ct = default);
}
