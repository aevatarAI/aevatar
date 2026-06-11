using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal abstract class WorkflowRunControlCommandTargetResolverBase<TCommand>
    : ICommandTargetResolver<TCommand, WorkflowRunControlCommandTarget, WorkflowRunControlStartError>
    where TCommand : IWorkflowRunControlCommand
{
    private readonly IWorkflowActorBindingReader _bindingReader;

    protected WorkflowRunControlCommandTargetResolverBase(
        IWorkflowActorBindingReader bindingReader)
    {
        _bindingReader = bindingReader ?? throw new ArgumentNullException(nameof(bindingReader));
    }

    public async Task<CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>> ResolveAsync(
        TCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var actorId = (command.ActorId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidActorId(actorId));
        }

        var runId = (command.RunId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(runId))
        {
            return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.InvalidRunId(actorId, runId));
        }

        var validationError = ValidateCommand(command, actorId, runId);
        if (validationError != null)
        {
            return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Failure(
                validationError);
        }

        var binding = await _bindingReader.GetAsync(actorId, ct);
        if (binding == null)
        {
            return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.ActorNotFound(actorId, runId));
        }

        if (binding?.ActorKind != WorkflowActorKind.Run)
        {
            return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.ActorNotWorkflowRun(actorId, runId));
        }

        if (string.IsNullOrWhiteSpace(binding.RunId))
        {
            return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.RunBindingMissing(actorId, runId));
        }

        var boundRunId = binding.RunId.Trim();
        if (!string.Equals(boundRunId, runId, StringComparison.Ordinal))
        {
            return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Failure(
                WorkflowRunControlStartError.RunBindingMismatch(actorId, runId, boundRunId));
        }

        return CommandTargetResolution<WorkflowRunControlCommandTarget, WorkflowRunControlStartError>.Success(
            new WorkflowRunControlCommandTarget(actorId, boundRunId));
    }

    protected virtual WorkflowRunControlStartError? ValidateCommand(
        TCommand command,
        string actorId,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = actorId;
        _ = runId;
        return null;
    }
}
