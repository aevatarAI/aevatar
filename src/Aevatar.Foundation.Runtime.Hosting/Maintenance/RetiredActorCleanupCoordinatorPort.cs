using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Persistence;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Hosting.Maintenance;

// Refactor (issue1056/r3-consensus): Old pattern: a hosted service replayed and
// appended EventStore marker streams to decide per-spec cleanup ownership.
// New principle: this narrow maintenance port persists typed coordinator events
// under the approved coordinator actor id and applies the coordinator contract.
public sealed class RetiredActorCleanupCoordinatorPort : IRetiredActorCleanupCoordinatorPort
{
    private readonly IEventStore _eventStore;

    public RetiredActorCleanupCoordinatorPort(IEventStore eventStore)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
    }

    public async Task<RetiredActorCleanupLeaseHandle?> TryAcquireAsync(
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

        _ = RetiredActorCleanupCoordinatorEnvelopeFactory.Create(command, command.SpecId);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadCoordinatorAsync(ct).ConfigureAwait(false);
            var state = snapshot.State;
            var specKey = command.SpecId;
            var requestedAt = command.RequestedAt.ToDateTimeOffset().ToUniversalTime();
            if (state.Leases.TryGetValue(specKey, out var existing) &&
                existing.Status == RetiredActorCleanupLeaseStatus.Active &&
                existing.ExpiresAt.ToDateTimeOffset().ToUniversalTime() > requestedAt)
            {
                await AppendCoordinatorEventAsync(
                    new RetiredActorCleanupLeaseAcquireRejectedEvent
                    {
                        SpecId = specKey,
                        OwnerId = command.OwnerId,
                        ActiveEpoch = existing.Epoch,
                        ActiveOwnerId = existing.OwnerId,
                        RejectedAt = command.RequestedAt.Clone(),
                    },
                    snapshot.Version,
                    ct).ConfigureAwait(false);
                return null;
            }

            var epoch = state.Leases.TryGetValue(specKey, out existing) ? existing.Epoch + 1 : 1;
            var acquired = new RetiredActorCleanupLeaseAcquiredEvent
            {
                Lease = new RetiredActorCleanupLeaseRecord
                {
                    SpecId = specKey,
                    Epoch = epoch,
                    Token = command.RequestedToken,
                    OwnerId = command.OwnerId,
                    Status = RetiredActorCleanupLeaseStatus.Active,
                    StartedAt = command.RequestedAt.Clone(),
                    ExpiresAt = command.ExpiresAt.Clone(),
                },
                StaleTakeover = existing is { Status: RetiredActorCleanupLeaseStatus.Active },
            };

            try
            {
                await AppendCoordinatorEventAsync(acquired, snapshot.Version, ct).ConfigureAwait(false);
                return new RetiredActorCleanupLeaseHandle(
                    specKey,
                    epoch,
                    command.RequestedToken,
                    command.OwnerId,
                    command.RequestedAt.ToDateTimeOffset().ToUniversalTime(),
                    command.ExpiresAt.ToDateTimeOffset().ToUniversalTime());
            }
            catch (EventStoreOptimisticConcurrencyException)
            {
            }
        }
    }

    public async Task<bool> CheckAsync(RetiredActorCleanupLeaseHandle lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        var snapshot = await ReadCoordinatorAsync(ct).ConfigureAwait(false);
        var valid = IsActiveOwner(snapshot.State, lease);
        await AppendCoordinatorEventAsync(
            new RetiredActorCleanupLeaseCheckedEvent
            {
                SpecId = lease.SpecId,
                Epoch = lease.Epoch,
                Token = lease.Token,
                OwnerId = lease.OwnerId,
                Valid = valid,
                CheckedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            snapshot.Version,
            ct).ConfigureAwait(false);
        return valid;
    }

    public async Task ReleaseAsync(RetiredActorCleanupLeaseHandle lease, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await ReadCoordinatorAsync(ct).ConfigureAwait(false);
            var releasedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
            IMessage evt = IsActiveOwner(snapshot.State, lease)
                ? new RetiredActorCleanupLeaseReleasedEvent
                {
                    SpecId = lease.SpecId,
                    Epoch = lease.Epoch,
                    Token = lease.Token,
                    OwnerId = lease.OwnerId,
                    ReleasedAt = releasedAt,
                }
                : new RetiredActorCleanupLeaseReleaseIgnoredEvent
                {
                    SpecId = lease.SpecId,
                    Epoch = lease.Epoch,
                    Token = lease.Token,
                    OwnerId = lease.OwnerId,
                    IgnoredAt = releasedAt,
                };

            try
            {
                await AppendCoordinatorEventAsync(evt, snapshot.Version, ct).ConfigureAwait(false);
                return;
            }
            catch (EventStoreOptimisticConcurrencyException)
            {
            }
        }
    }

    public async Task RecordFailureAsync(
        RetiredActorCleanupLeaseHandle lease,
        Exception exception,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(exception);

        var snapshot = await ReadCoordinatorAsync(ct).ConfigureAwait(false);
        if (!IsActiveOwner(snapshot.State, lease))
            return;

        await AppendCoordinatorEventAsync(
            new RetiredActorCleanupLeaseFailureRecordedEvent
            {
                SpecId = lease.SpecId,
                Epoch = lease.Epoch,
                Token = lease.Token,
                OwnerId = lease.OwnerId,
                Error = exception.Message,
                FailedAt = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            },
            snapshot.Version,
            ct).ConfigureAwait(false);
    }

    private async Task<CoordinatorSnapshot> ReadCoordinatorAsync(CancellationToken ct)
    {
        var events = await _eventStore
            .GetEventsAsync(RetiredActorCleanupCoordinatorEnvelopeFactory.CoordinatorActorId, ct: ct)
            .ConfigureAwait(false);
        var state = new RetiredActorCleanupCoordinatorState();
        foreach (var stateEvent in events)
        {
            if (stateEvent.EventData == null)
                continue;

            var evt = UnpackCoordinatorEvent(stateEvent.EventData);
            if (evt == null)
                continue;

            state = RetiredActorCleanupCoordinatorGAgent.ApplyEvent(state, evt);
        }

        return new CoordinatorSnapshot(
            state,
            events.Count == 0 ? 0 : events[^1].Version);
    }

    private Task AppendCoordinatorEventAsync(IMessage evt, long expectedVersion, CancellationToken ct) =>
        _eventStore.AppendAsync(
            RetiredActorCleanupCoordinatorEnvelopeFactory.CoordinatorActorId,
            [
                new StateEvent
                {
                    AgentId = RetiredActorCleanupCoordinatorEnvelopeFactory.CoordinatorActorId,
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = evt.Descriptor.FullName,
                    EventData = Any.Pack(evt),
                    Timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Version = expectedVersion + 1,
                },
            ],
            expectedVersion,
            ct);

    private static IMessage? UnpackCoordinatorEvent(Any payload)
    {
        if (payload.TryUnpack<RetiredActorCleanupLeaseAcquiredEvent>(out var acquired))
            return acquired;
        if (payload.TryUnpack<RetiredActorCleanupLeaseReleasedEvent>(out var released))
            return released;
        if (payload.TryUnpack<RetiredActorCleanupLeaseFailureRecordedEvent>(out var failed))
            return failed;
        if (payload.TryUnpack<RetiredActorCleanupLeaseAcquireRejectedEvent>(out var rejected))
            return rejected;
        if (payload.TryUnpack<RetiredActorCleanupLeaseCheckedEvent>(out var checkedEvent))
            return checkedEvent;
        if (payload.TryUnpack<RetiredActorCleanupLeaseReleaseIgnoredEvent>(out var ignored))
            return ignored;

        return null;
    }

    private static bool IsActiveOwner(
        RetiredActorCleanupCoordinatorState state,
        RetiredActorCleanupLeaseHandle lease) =>
        state.Leases.TryGetValue(lease.SpecId, out var record) &&
        record.Status == RetiredActorCleanupLeaseStatus.Active &&
        record.Epoch == lease.Epoch &&
        string.Equals(record.Token, lease.Token, StringComparison.Ordinal) &&
        string.Equals(record.OwnerId, lease.OwnerId, StringComparison.Ordinal);

    private static string NormalizeRequired(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private sealed record CoordinatorSnapshot(RetiredActorCleanupCoordinatorState State, long Version);
}
