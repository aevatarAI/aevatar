namespace Aevatar.CQRS.Projections.Orchestration;

/// <summary>
/// Manages projection run registration and actor stream subscription lifecycle.
/// </summary>
public interface IWorkflowExecutionProjectionSubscriptionRegistry
{
    Task RegisterAsync(WorkflowExecutionProjectionContext context, CancellationToken ct = default);

    Task UnregisterAsync(string actorId, string runId, CancellationToken ct = default);

    Task<bool> WaitForCompletionAsync(string runId, TimeSpan timeout, CancellationToken ct = default);
}
