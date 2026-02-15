using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Orchestration;
using Aevatar.Workflow.Projection.Orchestration;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunContext
{
    public required IActor Actor { get; init; }

    public required string WorkflowName { get; init; }

    public required WorkflowProjectionRun ProjectionRun { get; init; }

    public required IWorkflowRunEventSink Sink { get; init; }

    public string ActorId => Actor.Id;

    public string RunId => ProjectionRun.RunId;

    public WorkflowChatRunStarted ToStarted() => new(ActorId, WorkflowName, RunId);
}

internal sealed record WorkflowRunContextCreateResult(
    WorkflowChatRunStartError Error,
    WorkflowRunContext? Context);
