using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

// Refactor (iter18/cluster-005):
//   Old pattern: accepted-only target resolution allocated projection/live-sink dependencies
//   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
internal sealed class WorkflowRunAcceptedCommandTargetResolver
    : ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;

    public WorkflowRunAcceptedCommandTargetResolver(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowRunProvisioningPort runProvisioningPort)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
    }

    public async Task<CommandTargetResolution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>> ResolveAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct = default)
    {
        // Refactor (iter18/cluster-005):
        //   Old pattern: accepted-only dispatch reused interaction targets that owned live sinks
        //   New principle: accepted-only target split + NoOp binder default + receipt-only(no live sink acquired)
        ArgumentNullException.ThrowIfNull(command);

        if (WorkflowCallerCredentialTokens.IsInvalidCredentialSet(
                command.CallerCredential?.BearerToken,
                command.CallerCredential?.Kind ?? NyxIdCallerCredentialKind.Unspecified,
                command.CallerCredential?.SourceReadableUserBearerToken))
            return CommandTargetResolution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.InvalidCallerCredential);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(command, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
            return CommandTargetResolution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>.Failure(actorResolution.Error);

        return CommandTargetResolution<WorkflowRunAcceptedCommandTarget, WorkflowChatRunStartError>.Success(
            new WorkflowRunAcceptedCommandTarget(
                actorResolution.Target.ActorId,
                actorResolution.WorkflowNameForRun,
                actorResolution.Target.CreatedActorIds,
                _runProvisioningPort));
    }
}
