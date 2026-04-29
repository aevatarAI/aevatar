using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.GAgents.Channel.Identity;

/// <summary>
/// Runtime lease for the per-binding projection materialization scope. Mirrors
/// <c>ChannelBotRegistrationMaterializationRuntimeLease</c>; the projection
/// runtime uses the lease to track active scopes and bound contexts.
/// </summary>
public sealed class ExternalIdentityBindingMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<ExternalIdentityBindingMaterializationContext>
{
    public ExternalIdentityBindingMaterializationRuntimeLease(ExternalIdentityBindingMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public ExternalIdentityBindingMaterializationContext Context { get; }
}
