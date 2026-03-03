using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.Workflow.Application.Abstractions.Runs;

namespace Aevatar.Workflow.Projection.Orchestration;

public interface IWorkflowProjectionSinkFailurePolicy
    : IProjectionPortSinkFailurePolicy<WorkflowExecutionRuntimeLease, IEventSink<WorkflowRunEvent>, WorkflowRunEvent>
{
}
