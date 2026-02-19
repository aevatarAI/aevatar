using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Application.Orchestration;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunContext
{
    public required IActor WorkflowActor { get; init; }

    public required IActor ExecutionActor { get; init; }

    public required string WorkflowName { get; init; }

    public required WorkflowProjectionRun ProjectionRun { get; init; }

    public required IWorkflowRunEventSink Sink { get; init; }

    public string WorkflowActorId => WorkflowActor.Id;

    public string ExecutionActorId => ExecutionActor.Id;

    public string RunId => ProjectionRun.RunId;

    public WorkflowChatRunStarted ToStarted() => new(ExecutionActorId, WorkflowName, RunId);
}

internal sealed record WorkflowRunContextCreateResult(
    WorkflowChatRunStartError Error,
    WorkflowRunContext? Context);
