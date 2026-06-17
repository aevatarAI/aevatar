using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ConversationDeliveryMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ConversationDeliveryMaterializationContext>
{
    public ConversationDeliveryMaterializationRuntimeLease(ConversationDeliveryMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ConversationDeliveryMaterializationContext Context { get; }
}
