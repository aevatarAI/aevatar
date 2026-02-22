using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionLiveSinkForwarder
{
    ValueTask ForwardAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        IWorkflowRunEventSink sink,
        WorkflowRunEvent evt,
        CancellationToken ct = default);
}
