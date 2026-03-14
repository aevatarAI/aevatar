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

    protected override WorkflowRunControlStartError? ValidateCommand(
        WorkflowSignalCommand command,
        string actorId,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(command);
        return string.IsNullOrWhiteSpace(command.SignalName)
            ? WorkflowRunControlStartError.InvalidSignalName(actorId, runId, command.SignalName ?? string.Empty)
            : null;
    }
}
