namespace Aevatar.CQRS.Projection.WorkflowExecution;

/// <summary>
/// Builds workflow execution projection context from request-level inputs.
/// </summary>
public interface IWorkflowExecutionProjectionContextFactory
{
    WorkflowExecutionProjectionContext Create(
        string runId,
        string rootActorId,
        string workflowName,
        string input,
        DateTimeOffset startedAt);
}
