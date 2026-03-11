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
}
