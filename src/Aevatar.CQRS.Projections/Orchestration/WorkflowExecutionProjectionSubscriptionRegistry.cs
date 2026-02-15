using Microsoft.Extensions.Logging;

namespace Aevatar.CQRS.Projections.Orchestration;

/// <summary>
/// Workflow-execution specific alias over the generic projection subscription registry.
/// </summary>
public sealed class WorkflowExecutionProjectionSubscriptionRegistry
    : ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>,
      IWorkflowExecutionProjectionSubscriptionRegistry
{
    public WorkflowExecutionProjectionSubscriptionRegistry(
        IProjectionCoordinator<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>> coordinator,
        IActorStreamSubscriptionHub<EventEnvelope> subscriptionHub,
        IProjectionCompletionDetector<WorkflowExecutionProjectionContext>? completionDetector = null,
        ILogger<ProjectionSubscriptionRegistry<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>>>? logger = null)
        : base(
            coordinator,
            subscriptionHub,
            completionDetector ?? new WorkflowCompletedEventProjectionCompletionDetector<WorkflowExecutionProjectionContext>(),
            logger)
    {
    }
}
