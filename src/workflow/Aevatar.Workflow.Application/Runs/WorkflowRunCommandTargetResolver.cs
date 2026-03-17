using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTargetResolver
    : ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowExecutionMaterializationActivationPort _materializationActivationPort;
    private readonly IWorkflowRunActorPort _actorPort;

    public WorkflowRunCommandTargetResolver(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowExecutionMaterializationActivationPort materializationActivationPort,
        IWorkflowRunActorPort actorPort)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _materializationActivationPort = materializationActivationPort ?? throw new ArgumentNullException(nameof(materializationActivationPort));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
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
                _projectionPort,
                _materializationActivationPort,
                _actorPort));
    }
}
