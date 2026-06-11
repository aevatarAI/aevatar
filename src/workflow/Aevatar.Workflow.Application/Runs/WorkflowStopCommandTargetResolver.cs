using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowStopCommandTargetResolver
    : WorkflowRunControlCommandTargetResolverBase<WorkflowStopCommand>
{
    public WorkflowStopCommandTargetResolver(
        IWorkflowActorBindingReader bindingReader)
        : base(bindingReader)
    {
    }
}
