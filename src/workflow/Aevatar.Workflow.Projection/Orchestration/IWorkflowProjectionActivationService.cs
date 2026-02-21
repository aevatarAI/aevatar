namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionActivationService
{
    Task<WorkflowExecutionRuntimeLease> EnsureAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default);
}
