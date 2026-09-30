using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ExternalCallbackCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    public const string ProjectionKind = "channel-external-callback";
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        var payload = context.Published.StateEvent?.EventData;
        if (context.ActorType != typeof(ExternalCallbackGAgent) || payload is null ||
            (!payload.Is(ExternalCallbackStateChanged.Descriptor) && !payload.Is(ExternalCallbackWriteRejected.Descriptor)))
            return [];
        return [new()
        {
            LeaseType = typeof(ExternalCallbackMaterializationLease),
            StartRequest = new() { RootActorId = context.ActorId, ProjectionKind = ProjectionKind, Mode = ProjectionRuntimeMode.DurableMaterialization },
        }];
    }
}
