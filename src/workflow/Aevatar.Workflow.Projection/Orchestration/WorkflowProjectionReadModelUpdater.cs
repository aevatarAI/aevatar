using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionReadModelUpdater : IWorkflowProjectionReadModelUpdater
{
    private readonly IProjectionMaterializationRouter<WorkflowExecutionReport, string> _materializationRouter;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionReadModelUpdater(
        IProjectionMaterializationRouter<WorkflowExecutionReport, string> materializationRouter,
        IProjectionClock clock)
    {
        _materializationRouter = materializationRouter;
        _clock = clock;
    }

    public Task RefreshMetadataAsync(
        string actorId,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        var updatedAt = _clock.UtcNow;
        return _materializationRouter.MutateAsync(actorId, report =>
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
        return _materializationRouter.MutateAsync(actorId, report =>
        {
            if (report.CompletionStatus == WorkflowExecutionCompletionStatus.Running)
                report.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;

            if (report.EndedAt < report.StartedAt)
                report.EndedAt = updatedAt;

            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, updatedAt);
        }, ct);
    }
}
