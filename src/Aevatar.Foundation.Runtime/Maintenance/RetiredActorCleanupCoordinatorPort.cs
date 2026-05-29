using Aevatar.Foundation.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Maintenance;

// Refactor (issue1056/r3-consensus): Old pattern: a hosted service replayed and
// appended EventStore marker streams to decide per-spec cleanup ownership.
// New principle: this narrow maintenance port obtains the coordinator actor and
// invokes its actor-owned typed lease contract; it does not replay or append
// actor facts outside the actor boundary.
public sealed class RetiredActorCleanupCoordinatorPort : IRetiredActorCleanupCoordinatorPort
{
    public const string CoordinatorActorId = "maintenance.retired-actor-cleanup-coordinator";

    private readonly IActorRuntime _actorRuntime;

    public RetiredActorCleanupCoordinatorPort(IActorRuntime actorRuntime)
    {
        _actorRuntime = actorRuntime ?? throw new ArgumentNullException(nameof(actorRuntime));
    }

    public Task<RetiredActorCleanupLeaseHandle?> TryAcquireAsync(
        string specId,
        string ownerId,
        DateTimeOffset nowUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken ct = default)
    {
        var command = new RetiredActorCleanupAcquireCommand
        {
            SpecId = NormalizeRequired(specId, nameof(specId)),
            OwnerId = NormalizeRequired(ownerId, nameof(ownerId)),
            RequestedToken = Guid.NewGuid().ToString("N"),
            RequestedAt = Timestamp.FromDateTimeOffset(nowUtc.ToUniversalTime()),
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAtUtc.ToUniversalTime()),
        };

        return WithCoordinatorAsync(coordinator => coordinator.TryAcquireLeaseAsync(command, ct), ct);
    }

    public Task<bool> CheckAsync(RetiredActorCleanupLeaseHandle lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        return WithCoordinatorAsync(
            coordinator => coordinator.CheckLeaseAsync(new RetiredActorCleanupCheckCommand
            {
                SpecId = lease.SpecId,
                Epoch = lease.Epoch,
                Token = lease.Token,
                OwnerId = lease.OwnerId,
                CheckedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }, ct),
            ct);
    }

    public Task ReleaseAsync(RetiredActorCleanupLeaseHandle lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        return WithCoordinatorAsync(
            coordinator => coordinator.ReleaseLeaseAsync(new RetiredActorCleanupReleaseCommand
            {
                SpecId = lease.SpecId,
                Epoch = lease.Epoch,
                Token = lease.Token,
                OwnerId = lease.OwnerId,
                ReleasedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }, ct),
            ct);
    }

    public Task RecordFailureAsync(
        RetiredActorCleanupLeaseHandle lease,
        Exception exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(exception);

        return WithCoordinatorAsync(
            coordinator => coordinator.RecordFailureAsync(new RetiredActorCleanupFailureCommand
            {
                SpecId = lease.SpecId,
                Epoch = lease.Epoch,
                Token = lease.Token,
                OwnerId = lease.OwnerId,
                Error = exception.Message,
                FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            }, ct),
            ct);
    }

    private async Task<T> WithCoordinatorAsync<T>(
        Func<IRetiredActorCleanupCoordinatorActor, Task<T>> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();
        var actor = await GetOrCreateCoordinatorAsync(ct).ConfigureAwait(false);
        return await action(actor).ConfigureAwait(false);
    }

    private async Task WithCoordinatorAsync(
        Func<IRetiredActorCleanupCoordinatorActor, Task> action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();
        var actor = await GetOrCreateCoordinatorAsync(ct).ConfigureAwait(false);
        await action(actor).ConfigureAwait(false);
    }

    private async Task<IRetiredActorCleanupCoordinatorActor> GetOrCreateCoordinatorAsync(CancellationToken ct)
    {
        var actor = await _actorRuntime.GetAsync(CoordinatorActorId).ConfigureAwait(false)
                    ?? await _actorRuntime.CreateByKindAsync(
                        RetiredActorCleanupCoordinatorGAgent.AgentKind,
                        CoordinatorActorId,
                        ct).ConfigureAwait(false);

        if (actor.Agent is IRetiredActorCleanupCoordinatorActor coordinator)
            return coordinator;

        throw new InvalidOperationException(
            $"Actor '{CoordinatorActorId}' does not expose the retired actor cleanup coordinator contract.");
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }
}
