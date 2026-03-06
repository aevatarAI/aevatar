using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Application.Abstractions.Projections;

public interface IWorkflowExecutionProjectionLifecyclePort
    : IEventSinkProjectionLifecyclePort<IWorkflowExecutionProjectionLease, WorkflowRunEvent>
{
    Task<IWorkflowExecutionProjectionLease?> EnsureActorProjectionAsync(
        string rootActorId,
        string workflowName,
        string input,
        string commandId,
        CancellationToken ct = default);
}
