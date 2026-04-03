using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.Attributes;
using Aevatar.Foundation.Abstractions.MultiAgent;
using Aevatar.Foundation.Core.EventSourcing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.Foundation.Core.MultiAgent;

/// <summary>
/// Multi-agent task coordination actor. Provides atomic task claiming, dependency graph
/// management, and work-stealing. Per-team scope — actor ID should be team-scoped.
/// </summary>
public class TaskBoardGAgent : GAgentBase<TaskBoardState>
{
    [EventHandler]
    public async Task HandleCreateTask(CreateTaskCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        if (string.IsNullOrWhiteSpace(cmd.TaskId))
            return;

        if (State.Tasks.ContainsKey(cmd.TaskId))
            return;

        var sequence = State.NextTaskSequence;
        await PersistDomainEventAsync(new TaskCreatedEvent
        {
            TaskId = cmd.TaskId,
            Content = cmd.Content ?? string.Empty,
            ActiveForm = cmd.ActiveForm ?? string.Empty,
            Sequence = sequence,
            BlockedBy = { cmd.BlockedBy },
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleClaimTask(ClaimTaskCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (!State.Tasks.TryGetValue(cmd.TaskId, out var task))
            return;
        if (task.Status != MultiAgent.TaskStatus.Pending)
            return;
        if (task.BlockedBy.Count > 0)
            return;
        if (State.AgentCurrentTask.ContainsKey(cmd.AgentId))
            return;

        await PersistDomainEventAsync(new TaskClaimedEvent
        {
            TaskId = cmd.TaskId,
            AgentId = cmd.AgentId,
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleCompleteTask(CompleteTaskCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (!State.Tasks.TryGetValue(cmd.TaskId, out var task))
            return;
        if (task.Status != MultiAgent.TaskStatus.InProgress)
            return;
        if (!string.Equals(task.OwnerAgentId, cmd.AgentId, StringComparison.Ordinal))
            return;

        // Single event — ApplyTaskCompleted also handles auto-unblock inline
        await PersistDomainEventAsync(new TaskCompletedEvent
        {
            TaskId = cmd.TaskId,
            AgentId = cmd.AgentId,
            Output = cmd.Output ?? string.Empty,
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleFailTask(FailTaskCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (!State.Tasks.TryGetValue(cmd.TaskId, out var task))
            return;
        if (task.Status != MultiAgent.TaskStatus.InProgress)
            return;
        if (!string.Equals(task.OwnerAgentId, cmd.AgentId, StringComparison.Ordinal))
            return;

        await PersistDomainEventAsync(new TaskFailedEvent
        {
            TaskId = cmd.TaskId,
            AgentId = cmd.AgentId,
            Error = cmd.Error ?? string.Empty,
            OccurredAt = Timestamp.FromDateTime(DateTime.UtcNow),
        });
    }

    [EventHandler]
    public async Task HandleRequestWork(RequestWorkCommand cmd)
    {
        ArgumentNullException.ThrowIfNull(cmd);

        if (State.AgentCurrentTask.ContainsKey(cmd.AgentId))
            return;

        // Find lowest-sequence PENDING task with no remaining blockers
        TaskEntry? best = null;
        foreach (var entry in State.Tasks.Values)
        {
            if (entry.Status != MultiAgent.TaskStatus.Pending)
                continue;
            if (entry.BlockedBy.Count > 0)
                continue;
            if (best == null || entry.Sequence < best.Sequence)
                best = entry;
        }

        if (best == null)
            return;

        var now = Timestamp.FromDateTime(DateTime.UtcNow);
        await PersistDomainEventAsync(new TaskClaimedEvent
        {
            TaskId = best.TaskId,
            AgentId = cmd.AgentId,
            OccurredAt = now,
        });

        await SendToAsync(cmd.AgentId, new WorkAssignedEvent
        {
            TaskId = best.TaskId,
            AgentId = cmd.AgentId,
        });
    }

    protected override TaskBoardState TransitionState(TaskBoardState current, IMessage evt) =>
        StateTransitionMatcher
            .Match(current, evt)
            .On<TaskCreatedEvent>(ApplyTaskCreated)
            .On<TaskClaimedEvent>(ApplyTaskClaimed)
            .On<TaskCompletedEvent>(ApplyTaskCompleted)
            .On<TaskFailedEvent>(ApplyTaskFailed)
            .On<TaskUnblockedEvent>(ApplyTaskUnblocked)
            .OrCurrent();

    private static Timestamp ResolveTimestamp(Timestamp? eventTimestamp) =>
        eventTimestamp != null && eventTimestamp != new Timestamp()
            ? eventTimestamp
            : Timestamp.FromDateTime(DateTime.UtcNow);

    private static TaskBoardState ApplyTaskCreated(TaskBoardState state, TaskCreatedEvent evt)
    {
        var ts = ResolveTimestamp(evt.OccurredAt);
        var next = state.Clone();
        next.Tasks[evt.TaskId] = new TaskEntry
        {
            TaskId = evt.TaskId,
            Content = evt.Content,
            ActiveForm = evt.ActiveForm,
            Status = MultiAgent.TaskStatus.Pending,
            Sequence = evt.Sequence,
            CreatedAt = ts,
            UpdatedAt = ts,
            BlockedBy = { evt.BlockedBy },
        };
        next.NextTaskSequence = evt.Sequence + 1;
        return next;
    }

    private static TaskBoardState ApplyTaskClaimed(TaskBoardState state, TaskClaimedEvent evt)
    {
        var ts = ResolveTimestamp(evt.OccurredAt);
        var next = state.Clone();
        if (next.Tasks.TryGetValue(evt.TaskId, out var task))
        {
            var updated = task.Clone();
            updated.Status = MultiAgent.TaskStatus.InProgress;
            updated.OwnerAgentId = evt.AgentId;
            updated.UpdatedAt = ts;
            next.Tasks[evt.TaskId] = updated;
        }
        next.AgentCurrentTask[evt.AgentId] = evt.TaskId;
        return next;
    }

    private static TaskBoardState ApplyTaskCompleted(TaskBoardState state, TaskCompletedEvent evt)
    {
        var ts = ResolveTimestamp(evt.OccurredAt);
        var next = state.Clone();
        if (next.Tasks.TryGetValue(evt.TaskId, out var task))
        {
            var updated = task.Clone();
            updated.Status = MultiAgent.TaskStatus.Completed;
            updated.Output = evt.Output;
            updated.UpdatedAt = ts;
            next.Tasks[evt.TaskId] = updated;
        }
        next.AgentCurrentTask.Remove(evt.AgentId);

        // Inline auto-unblock: remove completed task from all dependents' BlockedBy
        foreach (var entry in next.Tasks)
        {
            if (entry.Value.BlockedBy.Contains(evt.TaskId))
            {
                var dep = entry.Value.Clone();
                dep.BlockedBy.Remove(evt.TaskId);
                dep.UpdatedAt = ts;
                next.Tasks[entry.Key] = dep;
            }
        }

        return next;
    }

    private static TaskBoardState ApplyTaskFailed(TaskBoardState state, TaskFailedEvent evt)
    {
        var ts = ResolveTimestamp(evt.OccurredAt);
        var next = state.Clone();
        if (next.Tasks.TryGetValue(evt.TaskId, out var task))
        {
            var updated = task.Clone();
            updated.Status = MultiAgent.TaskStatus.Failed;
            updated.Error = evt.Error;
            updated.UpdatedAt = ts;
            next.Tasks[evt.TaskId] = updated;
        }
        next.AgentCurrentTask.Remove(evt.AgentId);
        return next;
    }

    private static TaskBoardState ApplyTaskUnblocked(TaskBoardState state, TaskUnblockedEvent evt)
    {
        // Kept for backward compat with already-persisted events
        var ts = ResolveTimestamp(evt.OccurredAt);
        var next = state.Clone();
        if (next.Tasks.TryGetValue(evt.TaskId, out var task))
        {
            var updated = task.Clone();
            updated.BlockedBy.Remove(evt.CompletedDependency);
            updated.UpdatedAt = ts;
            next.Tasks[evt.TaskId] = updated;
        }
        return next;
    }
}
