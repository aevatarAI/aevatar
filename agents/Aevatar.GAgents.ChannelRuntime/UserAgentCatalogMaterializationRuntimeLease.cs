using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class UserAgentCatalogMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<UserAgentCatalogMaterializationContext>
{
    public UserAgentCatalogMaterializationRuntimeLease(UserAgentCatalogMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public UserAgentCatalogMaterializationContext Context { get; }
}
