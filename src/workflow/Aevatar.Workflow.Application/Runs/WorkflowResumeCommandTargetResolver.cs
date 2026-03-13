using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowResumeCommandTargetResolver
    : WorkflowRunControlCommandTargetResolverBase<WorkflowResumeCommand>
{
    public WorkflowResumeCommandTargetResolver(
        IActorRuntime runtime,
        IWorkflowActorBindingReader bindingReader)
        : base(runtime, bindingReader)
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
