using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Governance.Abstractions;
using Aevatar.GAgentService.Governance.Core.GAgents;

namespace Aevatar.GAgentService.Governance.Projection.Orchestration;

/// <summary>
/// Maps governance committed state events to existing durable projection scopes.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public sealed class ServiceGovernanceCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorType != typeof(ServiceConfigurationGAgent) ||
            context.Published.StateEvent?.EventData == null)
        {
            yield break;
        }

        var payload = context.Published.StateEvent.EventData;
        if (!payload.Is(ServiceBindingCreatedEvent.Descriptor) &&
            !payload.Is(ServiceBindingUpdatedEvent.Descriptor) &&
            !payload.Is(ServiceBindingRetiredEvent.Descriptor) &&
            !payload.Is(ServiceEndpointCatalogCreatedEvent.Descriptor) &&
            !payload.Is(ServiceEndpointCatalogUpdatedEvent.Descriptor) &&
            !payload.Is(ServicePolicyCreatedEvent.Descriptor) &&
            !payload.Is(ServicePolicyUpdatedEvent.Descriptor) &&
            !payload.Is(ServicePolicyRetiredEvent.Descriptor) &&
            !payload.Is(LegacyServiceConfigurationImportedEvent.Descriptor))
        {
            yield break;
        }

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(ServiceConfigurationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = ServiceGovernanceProjectionKinds.Configuration,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
    }
}
