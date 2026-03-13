using System.Runtime.ExceptionServices;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Core;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection.Configuration;
using Aevatar.Workflow.Projection.ReadModels;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Workflow.Projection.Orchestration;

internal sealed class WorkflowRunDetachedCleanupOutboxGAgent
    : GAgentBase<WorkflowRunDetachedCleanupOutboxState>
{
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
            .Take(Math.Max(1, batchSize))
            .ToList();

        foreach (var entry in dueEntries)
            await ReplayEntryAsync(entry);
    }

    protected override WorkflowRunDetachedCleanupOutboxState TransitionState(
        WorkflowRunDetachedCleanupOutboxState current,
        IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<WorkflowRunDetachedCleanupEnqueuedEvent>(ApplyEnqueued)
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

    private static WorkflowRunDetachedCleanupOutboxState ApplySucceeded(
        WorkflowRunDetachedCleanupOutboxState current,
        WorkflowRunDetachedCleanupSucceededEvent evt)
    {
        var next = current.Clone();
        if (!next.Entries.TryGetValue(evt.RecordId, out var existing))
            return next;

        var updated = existing.Clone();
        updated.CompletedAtUtc = evt.CompletedAtUtc;
        updated.LastError = string.Empty;
        next.Entries[evt.RecordId] = updated;
        return next;
    }

    private async Task ReplayEntryAsync(WorkflowRunDetachedCleanupOutboxEntry entry)
    {
        WorkflowActorSnapshot? snapshot;
        try
        {
            snapshot = await Services
                .GetRequiredService<IWorkflowExecutionProjectionQueryPort>()
                .GetActorSnapshotAsync(entry.ActorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ScheduleRetryAsync(entry, ex);
            return;
        }

        if (!HasTerminalCompletion(snapshot))
            return;

        try
        {
            await CleanupAsync(entry, snapshot!);
            await PersistDomainEventAsync(new WorkflowRunDetachedCleanupSucceededEvent
            {
                RecordId = entry.RecordId,
                CompletedAtUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            });
        }
        catch (Exception ex)
        {
            await ScheduleRetryAsync(entry, ex);
        }
    }

    private async Task CleanupAsync(
        WorkflowRunDetachedCleanupOutboxEntry entry,
        WorkflowActorSnapshot snapshot)
    {
        var contextFactory = Services.GetRequiredService<IWorkflowExecutionProjectionContextFactory>();
        var lifecycle = Services.GetRequiredService<IProjectionLifecycleService<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>();
        var readModelUpdater = Services.GetRequiredService<IWorkflowProjectionReadModelUpdater>();
        var ownershipCoordinator = Services.GetRequiredService<IProjectionOwnershipCoordinator>();
        var actorPort = Services.GetRequiredService<IWorkflowRunActorPort>();
        var workflowName = string.IsNullOrWhiteSpace(snapshot.WorkflowName)
            ? entry.WorkflowName
            : snapshot.WorkflowName;
        var startedAt = snapshot.LastUpdatedAt == default
            ? DateTimeOffset.UtcNow
            : snapshot.LastUpdatedAt;
        var context = contextFactory.Create(
            entry.ActorId,
            entry.CommandId,
            entry.ActorId,
            workflowName,
            input: string.Empty,
            startedAt);

        Exception? firstException = null;

        try
        {
            await lifecycle.StopAsync(context, CancellationToken.None);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            await readModelUpdater.MarkStoppedAsync(entry.ActorId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        try
        {
            await ownershipCoordinator.ReleaseAsync(entry.ActorId, entry.CommandId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            firstException ??= ex;
        }

        foreach (var actorId in entry.CreatedActorIds
                     .Where(static x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.Ordinal)
                     .Reverse())
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
