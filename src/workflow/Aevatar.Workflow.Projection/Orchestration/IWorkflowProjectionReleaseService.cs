namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionReleaseService
{
    Task ReleaseIfIdleAsync(
        WorkflowExecutionRuntimeLease runtimeLease,
        CancellationToken ct = default);
}
