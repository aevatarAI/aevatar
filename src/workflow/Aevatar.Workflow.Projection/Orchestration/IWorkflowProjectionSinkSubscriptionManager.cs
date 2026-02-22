using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionSinkSubscriptionManager
{
    Task AttachOrReplaceAsync(
        WorkflowExecutionRuntimeLease lease,
        IWorkflowRunEventSink sink,
        Func<WorkflowRunEvent, ValueTask> handler,
        CancellationToken ct = default);

    Task DetachAsync(
        WorkflowExecutionRuntimeLease lease,
        IWorkflowRunEventSink sink,
        CancellationToken ct = default);

    int GetSubscriptionCount(WorkflowExecutionRuntimeLease lease);
}
