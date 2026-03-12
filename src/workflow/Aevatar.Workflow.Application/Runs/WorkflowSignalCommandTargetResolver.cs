using Aevatar.Foundation.Abstractions;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Runs;

internal sealed class WorkflowSignalCommandTargetResolver
    : WorkflowRunControlCommandTargetResolverBase<WorkflowSignalCommand>
{
    public WorkflowSignalCommandTargetResolver(
        IActorRuntime runtime,
        IWorkflowActorBindingReader bindingReader)
        : base(runtime, bindingReader)
    {
    }
}
