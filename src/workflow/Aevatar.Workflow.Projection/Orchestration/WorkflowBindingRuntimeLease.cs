using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowBindingRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<WorkflowBindingProjectionContext>
{
    public WorkflowBindingRuntimeLease(WorkflowBindingProjectionContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public WorkflowBindingProjectionContext Context { get; }
}
