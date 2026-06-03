using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.GAgents.StreamingProxy;

public sealed class StreamingProxyCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorType != typeof(StreamingProxyGAgent) ||
            context.Published.StateEvent?.EventData == null)
        {
            return [];
        }

        return
        [
            new ProjectionActivationPlan
            {
                LeaseType = typeof(StreamingProxyCurrentStateRuntimeLease),
                StartRequest = new ProjectionScopeStartRequest
                {
                    RootActorId = context.ActorId,
                    ProjectionKind = StreamingProxyGAgent.ProjectionKind,
                    Mode = ProjectionRuntimeMode.DurableMaterialization,
                },
            },
        ];
    }
}
