using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.Workflow.Application.Abstractions.Runs;
using Aevatar.Workflow.Projection;
using Aevatar.Workflow.Projection.ReadModels;

namespace Aevatar.Workflow.Presentation.AGUIAdapter;

public abstract class WorkflowRunSessionEventProjectorBase
    : ProjectionSessionEventProjectorBase<WorkflowExecutionProjectionContext, IReadOnlyList<WorkflowExecutionTopologyEdge>, WorkflowRunEvent>
{
    protected WorkflowRunSessionEventProjectorBase(
        IProjectionSessionEventHub<WorkflowRunEvent> runEventStreamHub)
        : base(runEventStreamHub)
    {
    }
}
