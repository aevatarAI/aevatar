using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Projection.Contexts;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgentService.Projection.Orchestration;

/// <summary>
/// Maps service committed state events to existing durable projection scopes.
/// </summary>
// Refactor (iter18/cluster-006):
//   Old pattern: command-path projection activation facade with new actor/lifecycle phase
//   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
public sealed class ServiceCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Published.StateEvent?.EventData == null)
            return [];

        var payload = context.Published.StateEvent.EventData;
        return context.ActorType switch
        {
            var type when type == typeof(ServiceDefinitionGAgent) => DefinitionPlans(context.ActorId, payload),
            var type when type == typeof(ServiceRevisionCatalogGAgent) => RevisionPlans(context.ActorId),
            var type when type == typeof(ServiceDeploymentManagerGAgent) => DeploymentPlans(context.ActorId, payload),
            var type when type == typeof(ServiceServingSetManagerGAgent) => ServingSetPlans(context.ActorId, payload),
            var type when type == typeof(ServiceRolloutManagerGAgent) => RolloutPlans(context.ActorId),
            var type when type == typeof(ServiceRunGAgent) => ServiceRunPlans(context.ActorId),
            var type when type == typeof(LlmSessionGAgent) => LlmSessionPlans(context.ActorId),
            var type when type == typeof(ResponsesAgentToolStateGAgent) => ResponsesAgentToolPlans(context.ActorId),
            _ => [],
        };
    }

    private static IEnumerable<ProjectionActivationPlan> DefinitionPlans(string actorId, Any payload)
    {
        if (!payload.Is(ServiceDefinitionCreatedEvent.Descriptor) &&
            !payload.Is(ServiceDefinitionUpdatedEvent.Descriptor) &&
            !payload.Is(DefaultServingRevisionChangedEvent.Descriptor))
        {
            return [];
        }

        return
        [
            DurablePlan<ServiceCatalogProjectionContext>(
                actorId,
                ServiceProjectionKinds.Catalog),
        ];
    }

    private static IEnumerable<ProjectionActivationPlan> RevisionPlans(string actorId) =>
    [
        DurablePlan<ServiceRevisionCatalogProjectionContext>(
            actorId,
            ServiceProjectionKinds.Revisions),
    ];

    private static IEnumerable<ProjectionActivationPlan> DeploymentPlans(string actorId, Any payload)
    {
        if (!payload.Is(ServiceDeploymentActivatedEvent.Descriptor) &&
            !payload.Is(ServiceDeploymentDeactivatedEvent.Descriptor) &&
            !payload.Is(ServiceDeploymentHealthChangedEvent.Descriptor))
        {
            return [];
        }

        return
        [
            DurablePlan<ServiceDeploymentCatalogProjectionContext>(
                actorId,
                ServiceProjectionKinds.Deployments),
            DurablePlan<ServiceCatalogProjectionContext>(
                actorId,
                ServiceProjectionKinds.Catalog),
        ];
    }

    private static IEnumerable<ProjectionActivationPlan> ServingSetPlans(string actorId, Any payload)
    {
        if (!payload.Is(ServiceServingSetUpdatedEvent.Descriptor))
            return [];

        return
        [
            DurablePlan<ServiceServingSetProjectionContext>(
                actorId,
                ServiceProjectionKinds.Serving),
            DurablePlan<ServiceTrafficViewProjectionContext>(
                actorId,
                ServiceProjectionKinds.Traffic),
        ];
    }

    private static IEnumerable<ProjectionActivationPlan> RolloutPlans(string actorId) =>
    [
        DurablePlan<ServiceRolloutProjectionContext>(
            actorId,
            ServiceProjectionKinds.Rollouts),
    ];

    private static IEnumerable<ProjectionActivationPlan> ServiceRunPlans(string actorId) =>
    [
        DurablePlan<ServiceRunCurrentStateProjectionContext>(
            actorId,
            ServiceProjectionKinds.Runs),
    ];

    private static IEnumerable<ProjectionActivationPlan> LlmSessionPlans(string actorId) =>
    [
        DurablePlan<LlmSessionCurrentStateProjectionContext>(
            actorId,
            ServiceProjectionKinds.ResponseSessions),
    ];

    private static IEnumerable<ProjectionActivationPlan> ResponsesAgentToolPlans(string actorId) =>
    [
        DurablePlan<ResponsesAgentToolStateCurrentStateProjectionContext>(
            actorId,
            ServiceProjectionKinds.ResponsesAgentTools),
    ];

    private static ProjectionActivationPlan DurablePlan<TContext>(
        string actorId,
        string projectionKind)
        where TContext : class, IProjectionMaterializationContext =>
        new()
        {
            LeaseType = typeof(ServiceProjectionRuntimeLease<TContext>),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
}
