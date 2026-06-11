using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChatRouting;

public sealed class ChatRoutePolicyMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ChatRoutePolicyMaterializationContext>
{
    public ChatRoutePolicyMaterializationRuntimeLease(ChatRoutePolicyMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ChatRoutePolicyMaterializationContext Context { get; }
}
