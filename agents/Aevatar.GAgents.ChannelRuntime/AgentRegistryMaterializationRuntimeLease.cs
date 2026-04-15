using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class AgentRegistryMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<AgentRegistryMaterializationContext>
{
    public AgentRegistryMaterializationRuntimeLease(AgentRegistryMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public AgentRegistryMaterializationContext Context { get; }
}
