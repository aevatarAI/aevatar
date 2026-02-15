namespace Aevatar.CQRS.Projection.WorkflowExecution.Orchestration;

/// <summary>
/// Default workflow projection context factory.
/// </summary>
public sealed class DefaultWorkflowExecutionProjectionContextFactory
    : IWorkflowExecutionProjectionContextFactory
{
    public WorkflowExecutionProjectionContext Create(
        string runId,
        string rootActorId,
        string workflowName,
        string input,
        DateTimeOffset startedAt) =>
        new()
        {
            RunId = runId,
            RootActorId = rootActorId,
            WorkflowName = workflowName,
            StartedAt = startedAt,
            Input = input,
        };
}
