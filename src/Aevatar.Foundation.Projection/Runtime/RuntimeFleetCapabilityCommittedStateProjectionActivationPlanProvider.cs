using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.Foundation.Core.Runtime;

namespace Aevatar.Foundation.Projection.Runtime;

public sealed class RuntimeFleetCapabilityCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(
        CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorType != typeof(RuntimeFleetCapabilityAuthorityGAgent))
            yield break;

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(RuntimeFleetCapabilityProjectionRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = RuntimeFleetCapabilityProjectionKinds.AuthorityCurrentState,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
    }
}
