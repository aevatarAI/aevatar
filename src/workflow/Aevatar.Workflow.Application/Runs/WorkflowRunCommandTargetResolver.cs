using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Projections;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRunCommandTargetResolver
    : ICommandTargetResolver<WorkflowChatRunRequest, WorkflowRunCommandTarget, WorkflowChatRunStartError>
{
    private readonly IWorkflowRunActorResolver _actorResolver;
    private readonly IWorkflowExecutionProjectionPort _projectionPort;
    private readonly IWorkflowRunProvisioningPort _runProvisioningPort;
    private readonly WorkflowRunDurableCompletionResolver _durableCompletionResolver;

    public WorkflowRunCommandTargetResolver(
        IWorkflowRunActorResolver actorResolver,
        IWorkflowExecutionProjectionPort projectionPort,
        IWorkflowRunProvisioningPort runProvisioningPort,
        WorkflowRunDurableCompletionResolver durableCompletionResolver)
    {
        _actorResolver = actorResolver ?? throw new ArgumentNullException(nameof(actorResolver));
        _projectionPort = projectionPort ?? throw new ArgumentNullException(nameof(projectionPort));
        _runProvisioningPort = runProvisioningPort ?? throw new ArgumentNullException(nameof(runProvisioningPort));
        _durableCompletionResolver = durableCompletionResolver ?? throw new ArgumentNullException(nameof(durableCompletionResolver));
    }

    public async Task<CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>> ResolveAsync(
        WorkflowChatRunRequest command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_projectionPort.ProjectionEnabled)
            return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Failure(
                WorkflowChatRunStartError.ProjectionDisabled);

        if (command.TargetSeed != null)
            return ResolveSeededTarget(command, command.TargetSeed);

        var actorResolution = await _actorResolver.ResolveOrCreateAsync(command, ct);
        if (actorResolution.Error != WorkflowChatRunStartError.None || actorResolution.Target == null)
            return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Failure(actorResolution.Error);

        return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Success(
            new WorkflowRunCommandTarget(
                actorResolution.Target.ActorId,
                actorResolution.WorkflowNameForRun,
                actorResolution.Target.CreatedActorIds,
                _projectionPort,
                _runProvisioningPort,
                _durableCompletionResolver));
    }

    private CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError> ResolveSeededTarget(
        WorkflowChatRunRequest command,
        WorkflowRunTargetSeed seed)
    {
        var workflowValidation = ValidateSeedWorkflow(command, seed);
        if (workflowValidation != WorkflowChatRunStartError.None)
            return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Failure(workflowValidation);

        var sourceValidation = ValidateSeedSource(command, seed);
        if (sourceValidation != WorkflowChatRunStartError.None)
            return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Failure(sourceValidation);

        return CommandTargetResolution<WorkflowRunCommandTarget, WorkflowChatRunStartError>.Success(
            new WorkflowRunCommandTarget(
                seed.ActorId,
                seed.WorkflowNameForRun,
                seed.CreatedActorIds,
                _projectionPort,
                _runProvisioningPort,
                _durableCompletionResolver,
                destroyCreatedActorsOnDispatchFailure: false));
    }

    private static WorkflowChatRunStartError ValidateSeedWorkflow(
        WorkflowChatRunRequest command,
        WorkflowRunTargetSeed seed)
    {
        var requestedWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(command.Source?.WorkflowName);

        var seededWorkflowName = WorkflowRunNameNormalizer.NormalizeWorkflowName(seed.WorkflowNameForRun);
        if (!string.IsNullOrWhiteSpace(requestedWorkflowName) &&
            !string.Equals(requestedWorkflowName, seededWorkflowName, StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowChatRunStartError.WorkflowNameMismatch;
        }

        return WorkflowChatRunStartError.None;
    }

    private static WorkflowChatRunStartError ValidateSeedSource(
        WorkflowChatRunRequest command,
        WorkflowRunTargetSeed seed)
    {
        var requestedActorId = NormalizeActorId(command.Source?.ActorId);

        var seededActorId = NormalizeActorId(seed.Source?.ActorId);
        if (!string.IsNullOrWhiteSpace(requestedActorId) &&
            !string.IsNullOrWhiteSpace(seededActorId) &&
            !string.Equals(requestedActorId, seededActorId, StringComparison.Ordinal))
        {
            return WorkflowChatRunStartError.WorkflowBindingMismatch;
        }

        return WorkflowChatRunStartError.None;
    }

    private static string NormalizeActorId(string? actorId) =>
        string.IsNullOrWhiteSpace(actorId) ? string.Empty : actorId.Trim();
}
