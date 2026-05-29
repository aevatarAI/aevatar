using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.TypeSystem;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Runtime.Maintenance;

// Refactor (issue1056/impl): Old pattern: hosted-service EventStore marker replay/write. New principle: actor-owned cleanup lease via IActorDispatchPort + EventEnvelope + narrow command-result contract (Phase 9 r6 consensus).
[GAgent(Kind)]
public sealed class RetiredActorCleanupCoordinatorGAgent : GAgentBase<RetiredActorCleanupCoordinatorState>
{
    public const string Kind = "foundation.retired-actor-cleanup-coordinator";
    public const string ActorId = "foundation.retired-actor-cleanup-coordinator";

    private readonly IRetiredActorCleanupCoordinatorResultPort _resultPort;

    public RetiredActorCleanupCoordinatorGAgent(IRetiredActorCleanupCoordinatorResultPort resultPort)
    {
        _resultPort = resultPort ?? throw new ArgumentNullException(nameof(resultPort));
        InitializeId();
    }

    [EventHandler]
    public async Task HandleAcquireLeaseAsync(RetiredActorCleanupAcquireLeaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = command.RequestedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        RetiredActorCleanupAcquireLeaseResult result;
        if (!ValidateCommand(command.CommandId, command.SpecId, command.OwnerToken, command.ResultStreamId, out var invalidReason))
        {
            result = new RetiredActorCleanupAcquireLeaseResult
            {
                CommandId = command.CommandId ?? string.Empty,
                SpecId = command.SpecId ?? string.Empty,
                OwnerToken = command.OwnerToken ?? string.Empty,
                Status = RetiredActorCleanupAcquireLeaseStatus.Invalid,
                Message = invalidReason,
            };
        }
        else if (!State.Leases.TryGetValue(command.SpecId, out var current) ||
                 IsExpired(current, command.LeaseTimeoutSeconds, now) ||
                 string.Equals(current.OwnerToken, command.OwnerToken, StringComparison.Ordinal))
        {
            await PersistDomainEventAsync(new RetiredActorCleanupLeaseAcquiredEvent
            {
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                AcquiredAt = now.Clone(),
                HeartbeatAt = now.Clone(),
            }).ConfigureAwait(false);

            result = new RetiredActorCleanupAcquireLeaseResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupAcquireLeaseStatus.Granted,
                CurrentOwnerToken = command.OwnerToken,
                AcquiredAt = now.Clone(),
                HeartbeatAt = now.Clone(),
            };
        }
        else
        {
            result = new RetiredActorCleanupAcquireLeaseResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupAcquireLeaseStatus.Denied,
                CurrentOwnerToken = current.OwnerToken,
                AcquiredAt = current.AcquiredAt?.Clone(),
                HeartbeatAt = current.HeartbeatAt?.Clone(),
            };
        }

        await PublishResultAsync(command.ResultStreamId, new RetiredActorCleanupCoordinatorCommandResult
        {
            AcquireLease = result,
        }).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleCheckLeaseAsync(RetiredActorCleanupCheckLeaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = command.CheckedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        RetiredActorCleanupCheckLeaseResult result;
        if (!ValidateCommand(command.CommandId, command.SpecId, command.OwnerToken, command.ResultStreamId, out var invalidReason))
        {
            result = new RetiredActorCleanupCheckLeaseResult
            {
                CommandId = command.CommandId ?? string.Empty,
                SpecId = command.SpecId ?? string.Empty,
                OwnerToken = command.OwnerToken ?? string.Empty,
                Status = RetiredActorCleanupCheckLeaseStatus.Invalid,
                Message = invalidReason,
            };
        }
        else if (State.Leases.TryGetValue(command.SpecId, out var current) &&
                 string.Equals(current.OwnerToken, command.OwnerToken, StringComparison.Ordinal))
        {
            await PersistDomainEventAsync(new RetiredActorCleanupLeaseHeartbeatRecordedEvent
            {
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                HeartbeatAt = now.Clone(),
            }).ConfigureAwait(false);

            result = new RetiredActorCleanupCheckLeaseResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupCheckLeaseStatus.StillOwner,
                CurrentOwnerToken = command.OwnerToken,
                HeartbeatAt = now.Clone(),
            };
        }
        else
        {
            result = new RetiredActorCleanupCheckLeaseResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupCheckLeaseStatus.NotOwner,
                CurrentOwnerToken = current?.OwnerToken ?? string.Empty,
                HeartbeatAt = current?.HeartbeatAt?.Clone(),
            };
        }

        await PublishResultAsync(command.ResultStreamId, new RetiredActorCleanupCoordinatorCommandResult
        {
            CheckLease = result,
        }).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleReleaseLeaseAsync(RetiredActorCleanupReleaseLeaseCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = command.ReleasedAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        RetiredActorCleanupReleaseLeaseResult result;
        if (!ValidateCommand(command.CommandId, command.SpecId, command.OwnerToken, command.ResultStreamId, out var invalidReason))
        {
            result = new RetiredActorCleanupReleaseLeaseResult
            {
                CommandId = command.CommandId ?? string.Empty,
                SpecId = command.SpecId ?? string.Empty,
                OwnerToken = command.OwnerToken ?? string.Empty,
                Status = RetiredActorCleanupReleaseLeaseStatus.Invalid,
                Message = invalidReason,
            };
        }
        else if (State.Leases.TryGetValue(command.SpecId, out var current) &&
                 string.Equals(current.OwnerToken, command.OwnerToken, StringComparison.Ordinal))
        {
            await PersistDomainEventAsync(new RetiredActorCleanupLeaseReleasedEvent
            {
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                ReleasedAt = now.Clone(),
            }).ConfigureAwait(false);

            result = new RetiredActorCleanupReleaseLeaseResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupReleaseLeaseStatus.Released,
            };
        }
        else
        {
            result = new RetiredActorCleanupReleaseLeaseResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupReleaseLeaseStatus.NotOwner,
                CurrentOwnerToken = current?.OwnerToken ?? string.Empty,
            };
        }

        await PublishResultAsync(command.ResultStreamId, new RetiredActorCleanupCoordinatorCommandResult
        {
            ReleaseLease = result,
        }).ConfigureAwait(false);
    }

    [EventHandler]
    public async Task HandleRecordFailureAsync(RetiredActorCleanupRecordFailureCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var now = command.OccurredAt ?? Timestamp.FromDateTime(DateTime.UtcNow);
        RetiredActorCleanupRecordFailureResult result;
        if (!ValidateCommand(command.CommandId, command.SpecId, command.OwnerToken, command.ResultStreamId, out var invalidReason))
        {
            result = new RetiredActorCleanupRecordFailureResult
            {
                CommandId = command.CommandId ?? string.Empty,
                SpecId = command.SpecId ?? string.Empty,
                OwnerToken = command.OwnerToken ?? string.Empty,
                Status = RetiredActorCleanupRecordFailureStatus.Invalid,
                Message = invalidReason,
            };
        }
        else if (State.Leases.TryGetValue(command.SpecId, out var current) &&
                 string.Equals(current.OwnerToken, command.OwnerToken, StringComparison.Ordinal))
        {
            await PersistDomainEventAsync(new RetiredActorCleanupLeaseFailureRecordedEvent
            {
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Reason = command.Reason ?? string.Empty,
                OccurredAt = now.Clone(),
            }).ConfigureAwait(false);

            result = new RetiredActorCleanupRecordFailureResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupRecordFailureStatus.Recorded,
                CurrentOwnerToken = command.OwnerToken,
                FailureCount = State.Leases[command.SpecId].FailureCount,
            };
        }
        else
        {
            result = new RetiredActorCleanupRecordFailureResult
            {
                CommandId = command.CommandId,
                SpecId = command.SpecId,
                OwnerToken = command.OwnerToken,
                Status = RetiredActorCleanupRecordFailureStatus.Ignored,
                CurrentOwnerToken = current?.OwnerToken ?? string.Empty,
                FailureCount = current?.FailureCount ?? 0,
            };
        }

        await PublishResultAsync(command.ResultStreamId, new RetiredActorCleanupCoordinatorCommandResult
        {
            RecordFailure = result,
        }).ConfigureAwait(false);
    }

    protected override RetiredActorCleanupCoordinatorState TransitionState(
        RetiredActorCleanupCoordinatorState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<RetiredActorCleanupLeaseAcquiredEvent>(ApplyLeaseAcquired)
            .On<RetiredActorCleanupLeaseHeartbeatRecordedEvent>(ApplyHeartbeatRecorded)
            .On<RetiredActorCleanupLeaseReleasedEvent>(ApplyLeaseReleased)
            .On<RetiredActorCleanupLeaseFailureRecordedEvent>(ApplyFailureRecorded)
            .OrCurrent();

    private static RetiredActorCleanupCoordinatorState ApplyLeaseAcquired(
        RetiredActorCleanupCoordinatorState state,
        RetiredActorCleanupLeaseAcquiredEvent evt)
    {
        var next = state.Clone();
        next.Leases[evt.SpecId] = new RetiredActorCleanupLeaseEntry
        {
            SpecId = evt.SpecId,
            OwnerToken = evt.OwnerToken,
            AcquiredAt = evt.AcquiredAt?.Clone(),
            HeartbeatAt = evt.HeartbeatAt?.Clone(),
        };
        MarkApplied(next, evt.SpecId, "acquired");
        return next;
    }

    private static RetiredActorCleanupCoordinatorState ApplyHeartbeatRecorded(
        RetiredActorCleanupCoordinatorState state,
        RetiredActorCleanupLeaseHeartbeatRecordedEvent evt)
    {
        var next = state.Clone();
        if (next.Leases.TryGetValue(evt.SpecId, out var lease) &&
            string.Equals(lease.OwnerToken, evt.OwnerToken, StringComparison.Ordinal))
        {
            lease.HeartbeatAt = evt.HeartbeatAt?.Clone();
        }

        MarkApplied(next, evt.SpecId, "heartbeat");
        return next;
    }

    private static RetiredActorCleanupCoordinatorState ApplyLeaseReleased(
        RetiredActorCleanupCoordinatorState state,
        RetiredActorCleanupLeaseReleasedEvent evt)
    {
        var next = state.Clone();
        if (next.Leases.TryGetValue(evt.SpecId, out var lease) &&
            string.Equals(lease.OwnerToken, evt.OwnerToken, StringComparison.Ordinal))
        {
            next.Leases.Remove(evt.SpecId);
        }

        MarkApplied(next, evt.SpecId, "released");
        return next;
    }

    private static RetiredActorCleanupCoordinatorState ApplyFailureRecorded(
        RetiredActorCleanupCoordinatorState state,
        RetiredActorCleanupLeaseFailureRecordedEvent evt)
    {
        var next = state.Clone();
        if (next.Leases.TryGetValue(evt.SpecId, out var lease) &&
            string.Equals(lease.OwnerToken, evt.OwnerToken, StringComparison.Ordinal))
        {
            lease.FailureCount += 1;
            lease.LastFailureAt = evt.OccurredAt?.Clone();
            lease.LastFailureReason = evt.Reason ?? string.Empty;
        }

        MarkApplied(next, evt.SpecId, "failure");
        return next;
    }

    private static void MarkApplied(RetiredActorCleanupCoordinatorState state, string specId, string suffix)
    {
        state.LastAppliedEventVersion += 1;
        state.LastEventId = $"{specId}:{suffix}:{state.LastAppliedEventVersion}";
    }

    private static bool IsExpired(
        RetiredActorCleanupLeaseEntry lease,
        long timeoutSeconds,
        Timestamp now)
    {
        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : TimeSpan.Zero;
        if (timeout <= TimeSpan.Zero)
            return false;

        var heartbeat = lease.HeartbeatAt ?? lease.AcquiredAt;
        return heartbeat != null && now.ToDateTimeOffset() - heartbeat.ToDateTimeOffset() > timeout;
    }

    private static bool ValidateCommand(
        string commandId,
        string specId,
        string ownerToken,
        string resultStreamId,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            reason = "command_id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(specId))
        {
            reason = "spec_id is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(ownerToken))
        {
            reason = "owner_token is required.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(resultStreamId))
        {
            reason = "result_stream_id is required.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private Task PublishResultAsync(
        string resultStreamId,
        RetiredActorCleanupCoordinatorCommandResult result) =>
        string.IsNullOrWhiteSpace(resultStreamId)
            ? Task.CompletedTask
            : _resultPort.PublishAsync(resultStreamId, result, CancellationToken.None);
}
