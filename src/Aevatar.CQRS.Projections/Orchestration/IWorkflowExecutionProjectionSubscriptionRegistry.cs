namespace Aevatar.CQRS.Projections.Orchestration;

/// <summary>
/// Manages projection run registration and actor stream subscription lifecycle.
/// </summary>
public interface IWorkflowExecutionProjectionSubscriptionRegistry
    : IProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext>;
