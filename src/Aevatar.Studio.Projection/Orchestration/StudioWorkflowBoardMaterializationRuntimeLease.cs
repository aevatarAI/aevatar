using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Studio.Projection.Orchestration;

public sealed class StudioWorkflowBoardMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<StudioWorkflowBoardMaterializationContext>
{
    public StudioWorkflowBoardMaterializationRuntimeLease(StudioWorkflowBoardMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public StudioWorkflowBoardMaterializationContext Context { get; }
}
