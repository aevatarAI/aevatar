using Aevatar.Foundation.Abstractions;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

public sealed class WorkflowRunContext
{
    public required IActor RunActor { get; init; }

    public string? DefinitionActorId { get; init; }

    public required string WorkflowName { get; init; }

    public IEventSink<WorkflowRunEvent>? Sink { get; init; }

    public required string CommandId { get; init; }

    public required CommandContext CommandContext { get; init; }

    public IWorkflowExecutionProjectionLease? ProjectionLease { get; init; }

    public string RunActorId => RunActor.Id;

    public bool HasLiveDelivery => Sink != null && ProjectionLease != null;

    public WorkflowChatRunStarted ToStarted() => new(RunActorId, WorkflowName, CommandId, DefinitionActorId);
}

public sealed record WorkflowRunContextCreateResult(
    WorkflowChatRunStartError Error,
    WorkflowRunContext? Context);
