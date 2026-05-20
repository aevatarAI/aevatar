using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunAcceptedCommandTargetResolver
    : ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowRunActorPort _actorPort;

    public WorkflowRunAcceptedCommandTargetResolver(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowRunActorPort actorPort)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _actorPort = actorPort ?? throw new ArgumentNullException(nameof(actorPort));
    }

    public async Task<CommandTargetResolution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>> ResolveAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct = default)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: DefaultDetachedCommandDispatchService 在 accepted-only path 持有 live sink
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        ArgumentNullException.ThrowIfNull(command);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(command, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Actor == null)
            return CommandTargetResolution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>.Failure(actorResolution.Error);

        return CommandTargetResolution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>.Success(
            new WorkflowRunAcceptedCommandTarget(
                actorResolution.Actor,
                actorResolution.WorkflowNameForRun,
                actorResolution.CreatedActorIds,
                _actorPort));
    }
}
