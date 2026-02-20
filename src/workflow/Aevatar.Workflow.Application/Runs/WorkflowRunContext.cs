using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunContext
{
    public required IActor Actor { get; init; }

    public required string WorkflowName { get; init; }

    public required IWorkflowRunEventSink Sink { get; init; }

    public required string CommandId { get; init; }

    public required CommandContext CommandContext { get; init; }

    public required IWorkflowExecutionProjectionLease ProjectionLease { get; init; }

    public string ActorId => Actor.Id;

    public WorkflowChatRunStarted ToStarted() => new(ActorId, WorkflowName, CommandId);
}

internal sealed record WorkflowRunContextCreateResult(
    WorkflowChatRunStartError Error,
    WorkflowRunContext? Context);
