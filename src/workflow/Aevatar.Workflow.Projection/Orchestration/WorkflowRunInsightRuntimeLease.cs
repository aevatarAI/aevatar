using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowRunInsightRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<WorkflowRunInsightProjectionContext>
{
    public WorkflowRunInsightRuntimeLease(WorkflowRunInsightProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context;
    }

    public WorkflowRunInsightProjectionContext Context { get; }
}
