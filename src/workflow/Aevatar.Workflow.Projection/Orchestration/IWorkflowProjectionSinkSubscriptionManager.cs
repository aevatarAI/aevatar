using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.CQRS.Projection.Core.Abstractions;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionSinkSubscriptionManager
    : IProjectionPortSinkSubscriptionManager<WorkflowExecutionRuntimeLease, IWorkflowRunEventSink, WorkflowRunEvent>
{
    int GetSubscriptionCount(WorkflowExecutionRuntimeLease lease);
}
