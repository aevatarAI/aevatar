using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Workflow.Projection.Orchestration;

public sealed class WorkflowExecutionMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<WorkflowExecutionMaterializationContext>
{
    public WorkflowExecutionMaterializationRuntimeLease(WorkflowExecutionMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public WorkflowExecutionMaterializationContext Context { get; }
}
