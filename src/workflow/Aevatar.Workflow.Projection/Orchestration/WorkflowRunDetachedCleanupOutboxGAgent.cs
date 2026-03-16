using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Configuration;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowRunDetachedCleanupOutboxGAgent
    : GAgentBase<WorkflowRunDetachedCleanupOutboxState>
{
    private enum ReplayEntryOutcome
    {
        Pending = 0,
        Progressed = 1,
    }

    public const string ActorIdPrefix = "workflow.run.detached.cleanup.outbox";

    public static string BuildActorId(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("Scope id is required.", nameof(scopeId));

        return $"{ActorIdPrefix}:{scopeId.Trim()}";
    }

    public static string BuildRecordId(string actorId, string commandId)
    {
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("Actor id is required.", nameof(actorId));
        if (string.IsNullOrWhiteSpace(commandId))
            throw new ArgumentException("Command id is required.", nameof(commandId));

        return $"{actorId.Trim()}::{commandId.Trim()}";
    }

    [EventHandler]
    public async Task HandleEnqueueAsync(WorkflowRunDetachedCleanupEnqueuedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RecordId))
            throw new InvalidOperationException("Record id is required to enqueue detached cleanup.");
        if (string.IsNullOrWhiteSpace(evt.ActorId))
            throw new InvalidOperationException("Actor id is required to enqueue detached cleanup.");
        if (string.IsNullOrWhiteSpace(evt.CommandId))
            throw new InvalidOperationException("Command id is required to enqueue detached cleanup.");

        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleDiscardAsync(WorkflowRunDetachedCleanupDiscardedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RecordId))
            throw new InvalidOperationException("Record id is required to discard detached cleanup.");

        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleDispatchAcceptedAsync(WorkflowRunDetachedCleanupDispatchAcceptedEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        if (string.IsNullOrWhiteSpace(evt.RecordId))
            throw new InvalidOperationException("Record id is required to mark detached cleanup dispatch accepted.");

        await PersistDomainEventAsync(evt);
    }

    [EventHandler]
    public async Task HandleTriggerReplayAsync(WorkflowRunDetachedCleanupTriggerReplayEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        var utcNow = DateTime.UtcNow;
        var options = Services.GetRequiredService<WorkflowExecutionProjectionOptions>();
        var batchSize = evt.BatchSize > 0 ? evt.BatchSize : options.DetachedCleanupReplayBatchSize;

        var dueEntries = State.Entries.Values
            .Where(e => e.CompletedAtUtc == null && IsVisible(e, utcNow))
            .OrderBy(e => e.NextVisibleAtUtc?.ToDateTime() ?? DateTime.UnixEpoch)
            .ThenBy(e => e.EnqueuedAtUtc?.ToDateTime() ?? DateTime.UnixEpoch)
            .ToList();

        var remainingBudget = Math.Max(1, batchSize);
        foreach (var entry in dueEntries)
        {
            if (remainingBudget <= 0)
                break;

            if (await ReplayEntryAsync(entry) == ReplayEntryOutcome.Progressed)
                remainingBudget--;
        }
    }

    protected override WorkflowRunDetachedCleanupOutboxState TransitionState(
        WorkflowRunDetachedCleanupOutboxState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowRunDetachedCleanupEnqueuedEvent>(ApplyEnqueued)
            .On<WorkflowRunDetachedCleanupDiscardedEvent>(ApplyDiscarded)
            .On<WorkflowRunDetachedCleanupDispatchAcceptedEvent>(ApplyDispatchAccepted)
            .On<WorkflowRunDetachedCleanupRetryScheduledEvent>(ApplyRetryScheduled)
            .On<WorkflowRunDetachedCleanupSucceededEvent>(ApplySucceeded)
            .OrCurrent();

    private static WorkflowRunDetachedCleanupOutboxState ApplyEnqueued(
        WorkflowRunDetachedCleanupOutboxState current,
        WorkflowRunDetachedCleanupEnqueuedEvent evt)
    {
        var next = current.Clone();
        next.Entries[evt.RecordId] = new WorkflowRunDetachedCleanupOutboxEntry
        {
            RecordId = evt.RecordId,
            ActorId = evt.ActorId,
            WorkflowName = evt.WorkflowName,
            CommandId = evt.CommandId,
            CreatedActorIds = { evt.CreatedActorIds },
            EnqueuedAtUtc = evt.EnqueuedAtUtc,
            NextVisibleAtUtc = evt.EnqueuedAtUtc,
            AttemptCount = 0,
        };
        return next;
    }

    private static WorkflowRunDetachedCleanupOutboxState ApplyRetryScheduled(
        WorkflowRunDetachedCleanupOutboxState current,
        WorkflowRunDetachedCleanupRetryScheduledEvent evt)
    {
        var next = current.Clone();
        if (!next.Entries.TryGetValue(evt.RecordId, out var existing))
            return next;

        var updated = existing.Clone();
        updated.AttemptCount = Math.Max(1, evt.AttemptCount);
        updated.NextVisibleAtUtc = evt.NextVisibleAtUtc;
        updated.LastError = evt.Error;
        next.Entries[evt.RecordId] = updated;
        return next;
    }

    private static WorkflowRunDetachedCleanupOutboxState ApplyDispatchAccepted(
        WorkflowRunDetachedCleanupOutboxState current,
        WorkflowRunDetachedCleanupDispatchAcceptedEvent evt)
    {
        var next = current.Clone();
        if (!next.Entries.TryGetValue(evt.RecordId, out var existing))
            return next;

        var updated = existing.Clone();
        updated.DispatchAcceptedAtUtc = evt.DispatchAcceptedAtUtc;
        updated.NextVisibleAtUtc = evt.DispatchAcceptedAtUtc;
        updated.LastError = string.Empty;
        next.Entries[evt.RecordId] = updated;
        return next;
    }

    private static WorkflowRunDetachedCleanupOutboxState ApplyDiscarded(
        WorkflowRunDetachedCleanupOutboxState current,
        WorkflowRunDetachedCleanupDiscardedEvent evt)
    {
        var next = current.Clone();
        next.Entries.Remove(evt.RecordId);
        return next;
    }

    private static WorkflowRunDetachedCleanupOutboxState ApplySucceeded(
        WorkflowRunDetachedCleanupOutboxState current,
        WorkflowRunDetachedCleanupSucceededEvent evt)
    {
        var next = current.Clone();
        next.Entries.Remove(evt.RecordId);
        return next;
    }

    private async Task<ReplayEntryOutcome> ReplayEntryAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        var madeProgress = false;
        WorkflowActorSnapshot? snapshot;
        WorkflowActorProjectionState? projectionState;
        var queryPort = Services.GetRequiredService<IWorkflowExecutionProjectionQueryPort>();
        try
        {
            snapshot = await queryPort.GetActorSnapshotAsync(entry.ActorId, CancellationToken.None);
            projectionState = await queryPort.GetActorProjectionStateAsync(entry.ActorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ScheduleRetryAsync(entry, ex);
            return ReplayEntryOutcome.Progressed;
        }

        if (!HasDispatchAccepted(entry))
        {
            if (!CanConfirmDispatchAccepted(entry, projectionState))
            {
                await ScheduleRetryAsync(
                    entry,
                    CreateDispatchAcceptancePendingException(entry, projectionState));
                return ReplayEntryOutcome.Progressed;
            }

            try
            {
                entry = await MarkDispatchAcceptedAsync(entry);
                madeProgress = true;
            }
            catch (Exception ex)
            {
                await ScheduleRetryAsync(entry, ex);
                return ReplayEntryOutcome.Progressed;
            }
        }

        if (!HasTerminalCompletion(snapshot))
            return madeProgress ? ReplayEntryOutcome.Progressed : ReplayEntryOutcome.Pending;

        try
        {
            if (RequiresProjectionRelease(snapshot))
            {
                await CompleteReleasedEntryAsync(entry);
            }
            else
            {
                await CompleteDirectEntryAsync(entry);
            }

            return ReplayEntryOutcome.Progressed;
        }
        catch (TimeoutException ex) when (RequiresProjectionRelease(snapshot))
        {
            try
            {
                if (await TryCompleteReleasedEntryAfterAckTimeoutAsync(entry))
                    return ReplayEntryOutcome.Progressed;
            }
            catch (Exception recoveryEx)
            {
                await ScheduleRetryAsync(entry, recoveryEx);
                return ReplayEntryOutcome.Progressed;
            }

            await ScheduleRetryAsync(entry, ex);
            return ReplayEntryOutcome.Progressed;
        }
        catch (Exception ex)
        {
            await ScheduleRetryAsync(entry, ex);
            return ReplayEntryOutcome.Progressed;
        }
    }

    private async Task FinalizeDetachedCleanupAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        await FinalizeDetachedCleanupAsync(entry, entry.CommandId, releaseOwnership: true);
    }

    private async Task FinalizeDetachedCleanupAsync(
        WorkflowRunDetachedCleanupOutboxEntry entry,
        string ownershipSessionId,
        bool releaseOwnership)
    {
        var normalizedSessionId = string.IsNullOrWhiteSpace(ownershipSessionId)
            ? entry.CommandId
            : ownershipSessionId.Trim();

        Exception? firstException = null;

        try
        {
            await MarkReportStoppedAsync(entry.ActorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        if (releaseOwnership)
        {
            var ownershipCoordinator = Services.GetRequiredService<IProjectionOwnershipCoordinator>();
            try
            {
                await ownershipCoordinator.ReleaseAsync(entry.ActorId, normalizedSessionId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                firstException ??= ex;
            }
        }

        try
        {
            await DestroyCreatedActorsAsync(entry);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }

    private async Task MarkReportStoppedAsync(
        string actorId,
        CancellationToken ct)
    {
        var insightPort = Services.GetRequiredService<IWorkflowRunInsightActorPort>();
        await insightPort.MarkStoppedAsync(
            actorId,
            reason: "workflow_detached_cleanup",
            stoppedAt: DateTimeOffset.UtcNow,
            ct);
    }

    private async Task DestroyCreatedActorsAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        var actorPort = Services.GetRequiredService<IWorkflowRunActorPort>();
        var actorIds = entry.CreatedActorIds
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Array.Reverse(actorIds);

        Exception? firstException = null;

        foreach (var actorId in actorIds)
        {
            try
            {
                await actorPort.DestroyAsync(actorId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                firstException ??= new InvalidOperationException(
                    $"Failed to destroy workflow actor '{actorId}'.",
                    ex);
            }
        }

        if (firstException != null)
            ExceptionDispatchInfo.Capture(firstException).Throw();
    }

    private async Task RequestProjectionReleaseAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        var projectionControlHub = Services.GetRequiredService<IProjectionSessionEventHub<WorkflowProjectionControlEvent>>();
        var options = Services.GetRequiredService<WorkflowExecutionProjectionOptions>();
        var releaseCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscription = await projectionControlHub.SubscribeAsync(
            entry.ActorId,
            entry.CommandId,
            evt =>
            {
                if (evt.EventCase != WorkflowProjectionControlEvent.EventOneofCase.ReleaseCompleted)
                    return ValueTask.CompletedTask;

                var completed = evt.ReleaseCompleted;
                if (completed == null ||
                    !string.Equals(completed.ActorId, entry.ActorId, StringComparison.Ordinal) ||
                    !string.Equals(completed.CommandId, entry.CommandId, StringComparison.Ordinal))
                {
                    return ValueTask.CompletedTask;
                }

                releaseCompleted.TrySetResult(true);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        await projectionControlHub.PublishAsync(
            entry.ActorId,
            entry.CommandId,
            new WorkflowProjectionControlEvent
            {
                ReleaseRequested = new WorkflowProjectionReleaseRequestedEvent
                {
                    ActorId = entry.ActorId,
                    CommandId = entry.CommandId,
                },
            },
            CancellationToken.None);

        var timeout = TimeSpan.FromMilliseconds(Math.Max(100, options.DetachedCleanupReleaseAckTimeoutMs));
        await releaseCompleted.Task.WaitAsync(timeout);
    }

    private async Task CompleteDirectEntryAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        await FinalizeDetachedCleanupAsync(entry);
        await PersistDomainEventAsync(new WorkflowRunDetachedCleanupSucceededEvent
        {
            RecordId = entry.RecordId,
            CompletedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    private async Task CompleteReleasedEntryAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        await RequestProjectionReleaseAsync(entry);
        await FinalizeDetachedCleanupAsync(entry, entry.CommandId, releaseOwnership: false);
        await PersistDomainEventAsync(new WorkflowRunDetachedCleanupSucceededEvent
        {
            RecordId = entry.RecordId,
            CompletedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    private async Task<bool> TryCompleteReleasedEntryAfterAckTimeoutAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        var ownershipCoordinator = Services.GetRequiredService<IProjectionOwnershipCoordinator>();

        if (!await ownershipCoordinator.HasActiveLeaseAsync(entry.ActorId, entry.CommandId, CancellationToken.None))
        {
            await CompleteDirectEntryAsync(entry);
            return true;
        }

        return false;
    }

    private async Task ScheduleRetryAsync(
        WorkflowRunDetachedCleanupOutboxEntry entry,
        Exception exception)
    {
        var nextAttempt = entry.AttemptCount + 1;
        var delay = ComputeRetryDelay(nextAttempt);
        await PersistDomainEventAsync(new WorkflowRunDetachedCleanupRetryScheduledEvent
        {
            RecordId = entry.RecordId,
            AttemptCount = nextAttempt,
            NextVisibleAtUtc = Timestamp.FromDateTime(DateTime.UtcNow + delay),
            Error = $"{exception.GetType().Name}: {exception.Message}",
        });
    }

    private async Task<WorkflowRunDetachedCleanupOutboxEntry> MarkDispatchAcceptedAsync(
        WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        var acceptedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow);
        await PersistDomainEventAsync(new WorkflowRunDetachedCleanupDispatchAcceptedEvent
        {
            RecordId = entry.RecordId,
            DispatchAcceptedAtUtc = acceptedAtUtc,
        });

        return State.Entries.TryGetValue(entry.RecordId, out var updated)
            ? updated
            : entry;
    }

    private TimeSpan ComputeRetryDelay(int attempt)
    {
        var options = Services.GetRequiredService<WorkflowExecutionProjectionOptions>();
        var baseDelayMs = Math.Max(0, options.DetachedCleanupRetryBaseDelayMs);
        var maxDelayMs = Math.Max(baseDelayMs, options.DetachedCleanupRetryMaxDelayMs);
        if (baseDelayMs == 0)
            return TimeSpan.Zero;

        var exponent = Math.Min(10, Math.Max(0, attempt - 1));
        var nextDelay = (long)Math.Round(baseDelayMs * Math.Pow(2, exponent), MidpointRounding.AwayFromZero);
        return TimeSpan.FromMilliseconds(Math.Min(nextDelay, maxDelayMs));
    }

    private static bool HasTerminalCompletion(WorkflowActorSnapshot? snapshot) =>
        snapshot?.CompletionStatus is WorkflowRunCompletionStatus.Completed or
            WorkflowRunCompletionStatus.TimedOut or
            WorkflowRunCompletionStatus.Failed or
            WorkflowRunCompletionStatus.Stopped or
            WorkflowRunCompletionStatus.NotFound or
            WorkflowRunCompletionStatus.Disabled;

    private static bool RequiresProjectionRelease(WorkflowActorSnapshot? snapshot) =>
        snapshot?.CompletionStatus is WorkflowRunCompletionStatus.Completed or
            WorkflowRunCompletionStatus.TimedOut or
            WorkflowRunCompletionStatus.Failed;

    private static bool HasDispatchAccepted(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        return entry.DispatchAcceptedAtUtc != null;
    }

    private static Exception CreateDispatchAcceptancePendingException(
        WorkflowRunDetachedCleanupOutboxEntry entry,
        WorkflowActorProjectionState? projectionState)
    {
        if (projectionState == null)
        {
            return new InvalidOperationException(
                $"Detached cleanup is waiting for workflow projection state before dispatch acceptance can be confirmed. actorId='{entry.ActorId}', commandId='{entry.CommandId}'.");
        }

        if (!HasProjectedRuntimeEvidence(projectionState))
        {
            return new InvalidOperationException(
                $"Detached cleanup is waiting for projected workflow events before dispatch acceptance can be confirmed. actorId='{entry.ActorId}', commandId='{entry.CommandId}'.");
        }

        return new InvalidOperationException(
            $"Detached cleanup projection-state evidence does not match the expected command. actorId='{entry.ActorId}', commandId='{entry.CommandId}', projectedCommandId='{projectionState.LastCommandId}'.");
    }

    private static bool CanConfirmDispatchAccepted(
        WorkflowRunDetachedCleanupOutboxEntry entry,
        WorkflowActorProjectionState? projectionState)
    {
        if (projectionState == null)
            return false;

        if (!HasProjectedRuntimeEvidence(projectionState))
            return false;

        return string.IsNullOrWhiteSpace(projectionState.LastCommandId) ||
               string.Equals(
                   projectionState.LastCommandId,
                   entry.CommandId,
                   StringComparison.Ordinal);
    }

    private static bool HasProjectedRuntimeEvidence(WorkflowActorProjectionState projectionState) =>
        projectionState.StateVersion > 0 ||
        !string.IsNullOrWhiteSpace(projectionState.LastEventId);

    private static bool IsVisible(
        WorkflowRunDetachedCleanupOutboxEntry entry,
        DateTime utcNow)
    {
        if (entry.NextVisibleAtUtc == null)
            return true;

        var nextVisible = entry.NextVisibleAtUtc.ToDateTime();
        if (nextVisible.Kind != DateTimeKind.Utc)
            nextVisible = DateTime.SpecifyKind(nextVisible, DateTimeKind.Utc);

        return utcNow >= nextVisible;
    }
}
