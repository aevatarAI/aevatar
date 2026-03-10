using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTargetResolver
    : ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionLifecyclePort _projectionPort;

    public WorkflowRunCommandTargetResolver(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionLifecyclePort projectionPort)
    {
        _actorResolver = actorResolver;
        _projectionPort = projectionPort;
    }

    public async Task<CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>> ResolveAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_projectionPort.ProjectionEnabled)
            return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.ProjectionDisabled);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(command, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Actor == null)
            return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Failure(actorResolution.Error);

        return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Success(
            new WorkflowRunCommandTarget(
                actorResolution.Actor,
                actorResolution.WorkflowNameForRun,
                actorResolution.CreatedActorIds,
                _projectionPort));
    }
}
