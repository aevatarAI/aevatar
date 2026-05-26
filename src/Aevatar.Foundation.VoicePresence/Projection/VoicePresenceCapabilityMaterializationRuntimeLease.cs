using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;

namespace Aevatar.Foundation.VoicePresence.Projection;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: capability/session runtime facts were discovered through a process-local resolver path.
//   New principle: capability materialization owns a durable projection lease for actor-scoped voice read models.
public sealed class VoicePresenceCapabilityMaterializationRuntimeLease
    : ProjectionRuntimeLeaseBase,
      IProjectionContextRuntimeLease<VoicePresenceCapabilityMaterializationContext>
{
    public VoicePresenceCapabilityMaterializationRuntimeLease(
        VoicePresenceCapabilityMaterializationContext context)
        : base(context.RootActorId)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public VoicePresenceCapabilityMaterializationContext Context { get; }
}
