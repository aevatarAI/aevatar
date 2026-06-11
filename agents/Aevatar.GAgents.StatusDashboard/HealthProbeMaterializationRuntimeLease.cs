using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.StatusDashboard;

public sealed class HealthProbeMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<HealthProbeMaterializationContext>
{
    public HealthProbeMaterializationRuntimeLease(HealthProbeMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public HealthProbeMaterializationContext Context { get; }
}
