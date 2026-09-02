using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class ManagedCodexCredentialMaterializationRuntimeLease(
    ManagedCodexCredentialMaterializationContext context)
    : ProjectionRuntimeLeaseBase(context.RootActorId),
      IProjectionContextRuntimeLease<ManagedCodexCredentialMaterializationContext>
{
    public ManagedCodexCredentialMaterializationContext Context { get; } =
        context ?? throw new ArgumentNullException(nameof(context));
}
