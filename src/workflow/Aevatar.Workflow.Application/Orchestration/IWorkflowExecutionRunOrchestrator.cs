using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Queries;
using Aevatar.Workflow.Application.Abstractions.Runs;

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
    public WorkflowProjectionRun(WorkflowProjectionSession session) =>
        Session = session;

    public WorkflowProjectionSession Session { get; }

    public string RunId => Session.RunId;

    public WorkflowRunReport? WorkflowExecutionReport { get; set; }
}

public sealed record WorkflowProjectionFinalizeResult(
    WorkflowProjectionCompletionStatus ProjectionCompletionStatus,
    WorkflowRunReport? WorkflowExecutionReport)
{
    public bool ProjectionCompleted => ProjectionCompletionStatus == WorkflowProjectionCompletionStatus.Completed;
}
