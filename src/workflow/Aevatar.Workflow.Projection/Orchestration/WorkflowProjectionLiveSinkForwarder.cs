using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowProjectionLiveSinkForwarder
    : EventSinkProjectionLiveForwarder<WorkflowExecutionRuntimeLease, WorkflowRunEvent>,
      IWorkflowProjectionLiveSinkForwarder
{
    public WorkflowProjectionLiveSinkForwarder(IWorkflowProjectionSinkFailurePolicy sinkFailurePolicy)
        : base(sinkFailurePolicy)
    {
    }
}
