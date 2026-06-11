using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowResumeCommandTargetResolver
    : WorkflowRunControlCommandTargetResolverBase<WorkflowResumeCommand>
{
    public WorkflowResumeCommandTargetResolver(
        IWorkflowActorBindingReader bindingReader)
        : base(bindingReader)
    {
    }

    protected override WorkflowRunControlStartError? ValidateCommand(
        WorkflowResumeCommand command,
        string actorId,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(command);
        return string.IsNullOrWhiteSpace(command.StepId)
            ? WorkflowRunControlStartError.InvalidStepId(actorId, runId, command.StepId ?? string.Empty)
            : null;
    }
}
