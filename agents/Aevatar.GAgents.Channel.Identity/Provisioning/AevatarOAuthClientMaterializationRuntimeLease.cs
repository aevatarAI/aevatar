using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Channel.Identity;

public sealed class AevatarOAuthClientMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<AevatarOAuthClientMaterializationContext>
{
    public AevatarOAuthClientMaterializationRuntimeLease(AevatarOAuthClientMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public AevatarOAuthClientMaterializationContext Context { get; }
}
