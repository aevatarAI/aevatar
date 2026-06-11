using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.VoicePresence.Abstractions;

namespace Aevatar.Foundation.VoicePresence.Projection;

// Refactor (iter39/cluster-029-voice-presence-session-runtime-shape):
//   Old pattern: voice host capability lookup was detached from committed actor-state observation.
//   New principle: committed voice runtime state changes activate the existing durable projection materialization scope.
public sealed class VoicePresenceCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Published.StateEvent?.EventData?.Is(VoicePresenceRuntimeStateChangedEvent.Descriptor) != true)
            yield break;

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(VoicePresenceCapabilityMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = VoicePresenceProjectionKinds.CapabilityMaterialization,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
    }
}
