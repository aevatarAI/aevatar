using Aevatar.Workflow.Application.Abstractions.Schedules;
using Aevatar.Workflow.Application.Schedules;

namespace Aevatar.Workflow.Infrastructure.Schedules;

public sealed class InMemoryWorkflowScheduleStore : IWorkflowScheduleStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WorkflowScheduleDefinition> _schedules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkflowScheduleRunRecord> _runsByIdempotencyKey = new(StringComparer.Ordinal);

    public Task<WorkflowScheduleDefinition?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_schedules.GetValueOrDefault(scheduleId));
        }
    }

    public Task<IReadOnlyList<WorkflowScheduleDefinition>> ListAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WorkflowScheduleDefinition>>(_schedules.Values.ToList());
        }
    }

    public Task AddAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_schedules.TryAdd(definition.ScheduleId, definition))
                throw new InvalidOperationException($"Schedule '{definition.ScheduleId}' already exists.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(
        WorkflowScheduleDefinition definition,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_schedules.ContainsKey(definition.ScheduleId))
                throw new InvalidOperationException($"Schedule '{definition.ScheduleId}' does not exist.");

            _schedules[definition.ScheduleId] = definition;
        }

        return Task.CompletedTask;
    }

    public Task<WorkflowScheduleRunRecord?> GetRunAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_runsByIdempotencyKey.GetValueOrDefault(idempotencyKey));
        }
    }

    public Task AddRunAsync(
        WorkflowScheduleRunRecord run,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_runsByIdempotencyKey.TryAdd(run.IdempotencyKey, run))
                throw new InvalidOperationException($"Schedule run '{run.IdempotencyKey}' already exists.");
        }

        return Task.CompletedTask;
    }

    public Task UpdateRunAsync(
        WorkflowScheduleRunRecord run,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_runsByIdempotencyKey.ContainsKey(run.IdempotencyKey))
                throw new InvalidOperationException($"Schedule run '{run.IdempotencyKey}' does not exist.");

            _runsByIdempotencyKey[run.IdempotencyKey] = run;
        }

        return Task.CompletedTask;
    }
}
