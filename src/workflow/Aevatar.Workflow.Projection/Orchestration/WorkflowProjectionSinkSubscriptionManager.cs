using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionSinkSubscriptionManager
    : EventSinkProjectionSessionSubscriptionManager<WorkflowExecutionRuntimeLease, WorkflowRunEvent>,
      IWorkflowProjectionSinkSubscriptionManager
{
    public WorkflowProjectionSinkSubscriptionManager(
        IProjectionSessionEventHub<WorkflowRunEvent> sessionEventHub)
        : base(sessionEventHub)
    {
    }
}
