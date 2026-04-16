using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Studio.Projection.Orchestration;

public sealed class StudioMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<StudioMaterializationContext>
{
    public StudioMaterializationRuntimeLease(StudioMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public StudioMaterializationContext Context { get; }
}
