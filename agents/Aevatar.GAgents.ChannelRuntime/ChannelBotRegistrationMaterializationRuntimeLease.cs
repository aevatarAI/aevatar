using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.ChannelRuntime;

public sealed class ChannelBotRegistrationMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ChannelBotRegistrationMaterializationContext>
{
    public ChannelBotRegistrationMaterializationRuntimeLease(ChannelBotRegistrationMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ChannelBotRegistrationMaterializationContext Context { get; }
}
