using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionReadModelUpdater : IWorkflowProjectionReadModelUpdater
{
    private readonly IProjectionReadModelStore<WorkflowExecutionReport, string> _store;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionReadModelUpdater(
        IProjectionReadModelStore<WorkflowExecutionReport, string> store,
        IProjectionClock clock)
    {
        _store = store;
        _clock = clock;
    }

    public Task RefreshMetadataAsync(
        string actorId,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        return _store.MutateAsync(actorId, report =>
        {
            report.CommandId = context.CommandId;
            report.WorkflowName = context.WorkflowName;
            report.Input = context.Input;
            report.StartedAt = context.StartedAt;
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = report.StartedAt;

            report.DurationMs = Math.Max(0, (report.EndedAt - report.StartedAt).TotalMilliseconds);
        }, ct);
    }

    public Task MarkStoppedAsync(
        string actorId,
        CancellationToken ct = default)
    {
        return _store.MutateAsync(actorId, report =>
        {
            if (report.CompletionStatus == WorkflowExecutionCompletionStatus.Running)
                report.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;

            if (report.EndedAt < report.StartedAt)
                report.EndedAt = _clock.UtcNow;

            report.DurationMs = Math.Max(0, (report.EndedAt - report.StartedAt).TotalMilliseconds);
        }, ct);
    }
}
