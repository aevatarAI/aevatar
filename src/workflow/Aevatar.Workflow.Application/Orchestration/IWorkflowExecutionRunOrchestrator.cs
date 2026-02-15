using Aevatar.CQRS.Projection.Abstractions;
using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.Orchestration;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Application.Orchestration;

public interface IWorkflowExecutionRunOrchestrator
{
    Task<WorkflowProjectionRun> StartAsync(
        string actorId,
        string workflowName,
        string prompt,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);

    Task<WorkflowProjectionFinalizeResult> FinalizeAsync(
        WorkflowProjectionRun projectionRun,
        IActorRuntime runtime,
        string actorId,
        CancellationToken ct = default);

    Task RollbackAsync(
        WorkflowProjectionRun projectionRun,
        CancellationToken ct = default);
}

public sealed class WorkflowProjectionRun
{
    public WorkflowProjectionRun(WorkflowExecutionProjectionSession session) =>
        Session = session;

    public WorkflowExecutionProjectionSession Session { get; }

    public string RunId => Session.RunId;

    public WorkflowExecutionReport? WorkflowExecutionReport { get; set; }
}

public sealed record WorkflowProjectionFinalizeResult(
    ProjectionRunCompletionStatus ProjectionCompletionStatus,
    WorkflowExecutionReport? WorkflowExecutionReport)
{
    public bool ProjectionCompleted => ProjectionCompletionStatus == ProjectionRunCompletionStatus.Completed;
}
