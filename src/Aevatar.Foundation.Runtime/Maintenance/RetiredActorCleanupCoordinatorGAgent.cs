using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Maintenance;

// Refactor (issue1056/r3-consensus): Old pattern: RetiredActorCleanupHostedService
// owned a hidden EventStore marker state machine for per-spec leases.
// New principle: a long-lived coordinator actor owns the lease facts, including
// epoch fencing, stale takeover, owner release, and stale release ignore.
[GAgent(AgentKind)]
public sealed class RetiredActorCleanupCoordinatorGAgent
    : GAgentBase<RetiredActorCleanupCoordinatorState>, IRetiredActorCleanupCoordinatorActor
{
    public const string AgentKind = "foundation.retired-actor-cleanup-coordinator";

    [EventHandler]
    public Task HandleAcquireAsync(RetiredActorCleanupAcquireCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyAcquireAsync(command);
    }

    [EventHandler]
    public Task HandleCheckAsync(RetiredActorCleanupCheckCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyCheckAsync(command);
    }

    [EventHandler]
    public Task HandleReleaseAsync(RetiredActorCleanupReleaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyReleaseAsync(command);
    }

    [EventHandler]
    public Task HandleFailureAsync(RetiredActorCleanupFailureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return ApplyFailureAsync(command);
    }

    public Task<RetiredActorCleanupLeaseHandle?> TryAcquireLeaseAsync(
        RetiredActorCleanupAcquireCommand command,
        CancellationToken ct = default) =>
        TryAcquireLeaseCoreAsync(command, persist: true, ct);

    private async Task<RetiredActorCleanupLeaseHandle?> TryAcquireLeaseCoreAsync(
        RetiredActorCleanupAcquireCommand command,
        bool persist,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();

        var specId = NormalizeRequired(command.SpecId, nameof(command.SpecId));
        var ownerId = NormalizeRequired(command.OwnerId, nameof(command.OwnerId));
        var requestedToken = NormalizeRequired(command.RequestedToken, nameof(command.RequestedToken));
        var requestedAt = ToDateTimeOffset(command.RequestedAt);
        var expiresAt = ToDateTimeOffset(command.ExpiresAt);

        var hasExisting = State.Leases.TryGetValue(specId, out var existing);
        if (existing is { Status: RetiredActorCleanupLeaseStatus.Active } &&
            ToDateTimeOffset(existing.ExpiresAt) > requestedAt)
        {
            if (persist)
            {
                await PersistDomainEventAsync(new RetiredActorCleanupLeaseAcquireRejectedEvent
                {
                    SpecId = specId,
                    OwnerId = ownerId,
                    ActiveEpoch = existing.Epoch,
                    ActiveOwnerId = existing.OwnerId,
                    RejectedAt = Timestamp.FromDateTimeOffset(requestedAt),
                }, ct).ConfigureAwait(false);
            }

            return null;
        }

        var epoch = hasExisting ? existing!.Epoch + 1 : 1;
        var record = new RetiredActorCleanupLeaseRecord
        {
            SpecId = specId,
            Epoch = epoch,
            Token = requestedToken,
            OwnerId = ownerId,
            Status = RetiredActorCleanupLeaseStatus.Active,
            StartedAt = Timestamp.FromDateTimeOffset(requestedAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(expiresAt),
        };

        if (persist)
        {
            await PersistDomainEventAsync(new RetiredActorCleanupLeaseAcquiredEvent
            {
                Lease = record,
                StaleTakeover = existing is { Status: RetiredActorCleanupLeaseStatus.Active },
            }, ct).ConfigureAwait(false);
        }

        return new RetiredActorCleanupLeaseHandle(
            specId,
            epoch,
            requestedToken,
            ownerId,
            requestedAt,
            expiresAt);
    }

    public Task<bool> CheckLeaseAsync(RetiredActorCleanupCheckCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IsActiveOwner(command.SpecId, command.Epoch, command.Token, command.OwnerId));
    }

    public async Task ReleaseLeaseAsync(RetiredActorCleanupReleaseCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        await ApplyReleaseAsync(command).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(RetiredActorCleanupFailureCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ct.ThrowIfCancellationRequested();
        await ApplyFailureAsync(command).ConfigureAwait(false);
    }

    protected override RetiredActorCleanupCoordinatorState TransitionState(
        RetiredActorCleanupCoordinatorState current,
        IMessage evt) =>
        ApplyEvent(current, evt);

    public static RetiredActorCleanupCoordinatorState ApplyEvent(
        RetiredActorCleanupCoordinatorState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<RetiredActorCleanupLeaseAcquiredEvent>(ApplyAcquired)
            .On<RetiredActorCleanupLeaseReleasedEvent>(ApplyReleased)
            .On<RetiredActorCleanupLeaseFailureRecordedEvent>(ApplyFailureRecorded)
            .OrCurrent();

    private static RetiredActorCleanupCoordinatorState ApplyAcquired(
        RetiredActorCleanupCoordinatorState current,
        RetiredActorCleanupLeaseAcquiredEvent evt)
    {
        var next = current.Clone();
        if (evt.Lease != null)
            next.Leases[evt.Lease.SpecId] = evt.Lease.Clone();
        return next;
    }

    private static RetiredActorCleanupCoordinatorState ApplyReleased(
        RetiredActorCleanupCoordinatorState current,
        RetiredActorCleanupLeaseReleasedEvent evt)
    {
        var next = current.Clone();
        if (next.Leases.TryGetValue(evt.SpecId, out var releaseRecord) &&
            IsSameLease(releaseRecord, evt.Epoch, evt.Token, evt.OwnerId))
        {
            releaseRecord.Status = RetiredActorCleanupLeaseStatus.Released;
            releaseRecord.ReleasedAt = evt.ReleasedAt?.Clone();
            next.Leases[evt.SpecId] = releaseRecord;
        }

        return next;
    }

    private static RetiredActorCleanupCoordinatorState ApplyFailureRecorded(
        RetiredActorCleanupCoordinatorState current,
        RetiredActorCleanupLeaseFailureRecordedEvent evt)
    {
        var next = current.Clone();
        if (next.Leases.TryGetValue(evt.SpecId, out var failureRecord) &&
            IsSameLease(failureRecord, evt.Epoch, evt.Token, evt.OwnerId))
        {
            failureRecord.Status = RetiredActorCleanupLeaseStatus.Failed;
            failureRecord.ReleasedAt = evt.FailedAt?.Clone();
            failureRecord.LastError = evt.Error ?? string.Empty;
            next.Leases[evt.SpecId] = failureRecord;
        }

        return next;
    }

    private async Task ApplyAcquireAsync(RetiredActorCleanupAcquireCommand command)
    {
        await TryAcquireLeaseCoreAsync(command, persist: true).ConfigureAwait(false);
    }

    private Task ApplyCheckAsync(RetiredActorCleanupCheckCommand command) =>
        PersistDomainEventAsync(new RetiredActorCleanupLeaseCheckedEvent
        {
            SpecId = NormalizeRequired(command.SpecId, nameof(command.SpecId)),
            Epoch = command.Epoch,
            Token = command.Token ?? string.Empty,
            OwnerId = command.OwnerId ?? string.Empty,
            Valid = IsActiveOwner(command.SpecId, command.Epoch, command.Token, command.OwnerId),
            CheckedAt = command.CheckedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });

    private Task ApplyReleaseAsync(RetiredActorCleanupReleaseCommand command)
    {
        var specId = NormalizeRequired(command.SpecId, nameof(command.SpecId));
        var releasedAt = command.ReleasedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
        if (!IsActiveOwner(specId, command.Epoch, command.Token, command.OwnerId))
        {
            return PersistDomainEventAsync(new RetiredActorCleanupLeaseReleaseIgnoredEvent
            {
                SpecId = specId,
                Epoch = command.Epoch,
                Token = command.Token ?? string.Empty,
                OwnerId = command.OwnerId ?? string.Empty,
                IgnoredAt = releasedAt.Clone(),
            });
        }

        return PersistDomainEventAsync(new RetiredActorCleanupLeaseReleasedEvent
        {
            SpecId = specId,
            Epoch = command.Epoch,
            Token = command.Token ?? string.Empty,
            OwnerId = command.OwnerId ?? string.Empty,
            ReleasedAt = releasedAt,
        });
    }

    private Task ApplyFailureAsync(RetiredActorCleanupFailureCommand command)
    {
        var specId = NormalizeRequired(command.SpecId, nameof(command.SpecId));
        if (!IsActiveOwner(specId, command.Epoch, command.Token, command.OwnerId))
            return Task.CompletedTask;

        return PersistDomainEventAsync(new RetiredActorCleanupLeaseFailureRecordedEvent
        {
            SpecId = specId,
            Epoch = command.Epoch,
            Token = command.Token ?? string.Empty,
            OwnerId = command.OwnerId ?? string.Empty,
            Error = command.Error ?? string.Empty,
            FailedAt = command.FailedAt?.Clone() ?? Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
        });
    }

    private bool IsActiveOwner(string? specId, long epoch, string? token, string? ownerId)
    {
        if (string.IsNullOrWhiteSpace(specId))
            return false;

        return State.Leases.TryGetValue(specId.Trim(), out var lease) &&
               lease.Status == RetiredActorCleanupLeaseStatus.Active &&
               IsSameLease(lease, epoch, token, ownerId);
    }

    private static bool IsSameLease(
        RetiredActorCleanupLeaseRecord lease,
        long epoch,
        string? token,
        string? ownerId) =>
        lease.Epoch == epoch &&
        string.Equals(lease.Token, token, StringComparison.Ordinal) &&
        string.Equals(lease.OwnerId, ownerId, StringComparison.Ordinal);

    private static string NormalizeRequired(string? value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static DateTimeOffset ToDateTimeOffset(Timestamp? value) =>
        value?.ToDateTimeOffset().ToUniversalTime()
        ?? throw new ArgumentException("Timestamp is required.");
}
