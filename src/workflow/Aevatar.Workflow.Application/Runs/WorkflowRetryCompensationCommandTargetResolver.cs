using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowRetryCompensationCommandTargetResolver
    : WorkflowRunControlCommandTargetResolverBase<WorkflowRetryCompensationCommand>
{
    public WorkflowRetryCompensationCommandTargetResolver(
        IWorkflowActorBindingReader bindingReader)
        : base(bindingReader)
    {
    }

    protected override WorkflowRunControlStartError? ValidateCommand(
        WorkflowRetryCompensationCommand command,
        string actorId,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(command);
        return string.IsNullOrWhiteSpace(command.FailedCompensationStepId)
            ? WorkflowRunControlStartError.InvalidStepId(actorId, runId, command.FailedCompensationStepId ?? string.Empty)
            : null;
    }
}
