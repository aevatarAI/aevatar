using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTargetResolver
    : ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowExecutionReadModelActivationPort _readModelActivationPort;
    private readonly IWorkflowRunActorPort _actorPort;
    private readonly IWorkflowRunDetachedCleanupScheduler _cleanupScheduler;

    public WorkflowRunCommandTargetResolver(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowExecutionReadModelActivationPort readModelActivationPort,
        IWorkflowRunActorPort actorPort,
        IWorkflowRunDetachedCleanupScheduler cleanupScheduler)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _readModelActivationPort = readModelActivationPort ?? throw new ArgumentNullException(nameof(readModelActivationPort));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
        _cleanupScheduler = cleanupScheduler ?? throw new ArgumentNullException(nameof(cleanupScheduler));
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
                _readModelActivationPort,
                _actorPort,
                _cleanupScheduler));
    }
}
