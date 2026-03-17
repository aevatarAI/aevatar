using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowStopCommandTargetResolver
    : WorkflowRunControlCommandTargetResolverBase<WorkflowStopCommand>
{
    public WorkflowStopCommandTargetResolver(
        IActorRuntime runtime,
        IWorkflowActorBindingReader bindingReader)
        : base(runtime, bindingReader)
    {
    }
}
