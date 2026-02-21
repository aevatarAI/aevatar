using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionSinkFailurePolicy
{
    ValueTask<bool> TryHandleAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IWorkflowRunEventSink sink,
        WorkflowRunEvent sourceEvent,
        Exception exception,
        CancellationToken ct = default);
}
