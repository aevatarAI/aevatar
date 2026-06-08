using Aevatar.Workflow.Application.Abstractions.Schedules;

namespace Aevatar.Workflow.Application.Schedules;

public interface IWorkflowScheduleStore
{
    Task<WorkflowScheduleDefinition?> GetAsync(
        string scheduleId,
        CancellationToken ct = default);

    Task<IReadOnlyList<WorkflowScheduleDefinition>> ListAsync(
        CancellationToken ct = default);

    Task AddAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default);

    Task UpdateAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default);

    Task<WorkflowScheduleRunRecord?> GetRunAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task AddRunAsync(
        WorkflowScheduleRunRecord run,
        CancellationToken ct = default);

    Task UpdateRunAsync(
        WorkflowScheduleRunRecord run,
        CancellationToken ct = default);
}

public interface IWorkflowScheduleWakeupScheduler
{
    Task ScheduleAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default);

    Task CancelAsync(
        string scheduleId,
        CancellationToken ct = default);
}

public sealed class NoopWorkflowScheduleWakeupScheduler : IWorkflowScheduleWakeupScheduler
{
    public Task ScheduleAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task CancelAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleId);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
