using Aevatar.Foundation.Abstractions;
using Aevatar.Foundation.Abstractions.EventModules;
using Aevatar.Workflow.Core.Execution;
using Aevatar.Workflow.Core.Primitives;

namespace Aevatar.Workflow.Core.Modules;

internal sealed class WorkflowLoopModule
{
    private WorkflowDefinition? _workflow;

    public string Name => "workflow_execution_kernel";

    public int Priority => 0;

    public void SetWorkflow(WorkflowDefinition workflow) =>
        _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));

    public bool CanHandle(EventEnvelope envelope)
    {
        var payload = envelope.Payload;
        return payload != null &&
               (payload.Is(StartWorkflowEvent.Descriptor) ||
                payload.Is(StepCompletedEvent.Descriptor) ||
                payload.Is(WorkflowStepTimeoutFiredEvent.Descriptor) ||
                payload.Is(WorkflowStepRetryBackoffFiredEvent.Descriptor));
    }

    public Task HandleAsync(EventEnvelope envelope, IEventHandlerContext ctx, CancellationToken ct)
    {
        if (_workflow == null)
            return Task.CompletedTask;

        if (ctx.Agent is not IWorkflowExecutionStateHost host)
            throw new InvalidOperationException("Workflow execution state host is required for workflow loop tests.");

        return new WorkflowExecutionKernel(_workflow, host).HandleAsync(envelope, ctx, ct);
    }
}
