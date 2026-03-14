using Aevatar.Workflow.Projection.ReadModels;
using Aevatar.Workflow.Projection.Reducers;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionReadModelUpdater : IWorkflowProjectionReadModelUpdater
{
    private readonly IProjectionStoreDispatcher<WorkflowExecutionReport, string> _storeDispatcher;
    private readonly IProjectionClock _clock;

    public WorkflowProjectionReadModelUpdater(
        IProjectionStoreDispatcher<WorkflowExecutionReport, string> storeDispatcher,
        IProjectionClock clock)
    {
        _storeDispatcher = storeDispatcher;
        _clock = clock;
    }

    public Task RefreshMetadataAsync(
        string actorId,
        WorkflowExecutionProjectionContext context,
        CancellationToken ct = default)
    {
        var updatedAt = _clock.UtcNow;
        return _storeDispatcher.MutateAsync(actorId, report =>
        {
            report.Id = actorId;
            if (string.IsNullOrWhiteSpace(report.RootActorId))
                report.RootActorId = actorId;
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
        return _storeDispatcher.MutateAsync(actorId, report =>
        {
            report.Id = actorId;
            if (string.IsNullOrWhiteSpace(report.RootActorId))
                report.RootActorId = actorId;
            if (report.CompletionStatus is WorkflowExecutionCompletionStatus.Running or WorkflowExecutionCompletionStatus.Unknown)
                report.CompletionStatus = WorkflowExecutionCompletionStatus.Stopped;

            if (report.EndedAt < report.StartedAt)
                report.EndedAt = updatedAt;

            WorkflowExecutionProjectionMutations.RefreshDerivedFields(report, updatedAt);
        }, ct);
    }
}
