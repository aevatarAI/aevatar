using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;

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
        var updatedAt = _clock.UtcNow;
        return _store.MutateAsync(actorId, report =>
        {
            report.CommandId = context.CommandId;
            report.WorkflowName = context.WorkflowName;
            report.Input = context.Input;
            if (report.CreatedAt == default)
                report.CreatedAt = context.StartedAt;
            report.StartedAt = context.StartedAt;
            if (report.EndedAt < report.StartedAt)
                report.EndedAt = report.StartedAt;

            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, updatedAt);
        }, ct);
    }

    public Task MarkStoppedAsync(
        string actorId,
        CancellationToken ct = default)
    {
        var updatedAt = _clock.UtcNow;
        return _store.MutateAsync(actorId, report =>
        {
            if (report.CompletionStatus == WorkflowExecutionCompletionStatus.Running)
                report.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;

            if (report.EndedAt < report.StartedAt)
                report.EndedAt = updatedAt;

            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, updatedAt);
        }, ct);
    }
}
