using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionSinkSubscriptionManager
    : IProjectionPortSinkSubscriptionManager<WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent>
{
    int GetSubscriptionCount(WorkflowExecutionRuntimeLease lease);
}
