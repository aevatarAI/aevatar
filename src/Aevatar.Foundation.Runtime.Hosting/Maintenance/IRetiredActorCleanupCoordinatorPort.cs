namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

public interface IRetiredActorCleanupCoordinatorPort
{
    Task<RetiredActorCleanupLeaseHandle?> TryAcquireAsync(
        string specId,
        string ownerId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct = default);

    Task<bool> CheckAsync(RetiredActorCleanupLeaseHandle lease, CancellationToken ct = default);

    Task ReleaseAsync(RetiredActorCleanupLeaseHandle lease, CancellationToken ct = default);

    Task RecordFailureAsync(
        RetiredActorCleanupLeaseHandle lease,
        Exception exception,
        CancellationToken ct = default);
}

public sealed record RetiredActorCleanupLeaseHandle(
    string SpecId,
    long Epoch,
    string Token,
    string OwnerId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset ExpiresAtUtc);
