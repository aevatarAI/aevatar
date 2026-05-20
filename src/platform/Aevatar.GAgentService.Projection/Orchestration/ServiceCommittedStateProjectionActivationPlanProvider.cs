using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Core.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Projection.Contexts;

namespace Aevatar.GAgentService.Projection.Orchestration;

/// <summary>
/// Maps service committed state events to existing durable projection scopes.
/// </summary>
public sealed class ServiceCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    // Refactor (iter18/cluster-006):
    //   Old pattern: command-path projection activation facade with new actor/lifecycle phase
    //   New principle: committed-state publication hook activates existing projection scopes; no new actor/lifecycle phase
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Published.StateEvent?.EventData == null)
            yield break;

        var payload = context.Published.StateEvent.EventData;
        if (context.ActorType == typeof(ServiceDefinitionGAgent))
        {
            if (payload.Is(ServiceDefinitionCreatedEvent.Descriptor) ||
                payload.Is(ServiceDefinitionUpdatedEvent.Descriptor) ||
                payload.Is(DefaultServingRevisionChangedEvent.Descriptor))
            {
                yield return DurablePlan<ServiceCatalogProjectionContext>(
                    context.ActorId,
                    ServiceProjectionKinds.Catalog);
            }

            yield break;
        }

        if (context.ActorType == typeof(ServiceRevisionCatalogGAgent))
        {
            yield return DurablePlan<ServiceRevisionCatalogProjectionContext>(
                context.ActorId,
                ServiceProjectionKinds.Revisions);
            yield break;
        }

        if (context.ActorType == typeof(ServiceDeploymentManagerGAgent))
        {
            if (payload.Is(ServiceDeploymentActivatedEvent.Descriptor) ||
                payload.Is(ServiceDeploymentDeactivatedEvent.Descriptor) ||
                payload.Is(ServiceDeploymentHealthChangedEvent.Descriptor))
            {
                yield return DurablePlan<ServiceDeploymentCatalogProjectionContext>(
                    context.ActorId,
                    ServiceProjectionKinds.Deployments);
                yield return DurablePlan<ServiceCatalogProjectionContext>(
                    context.ActorId,
                    ServiceProjectionKinds.Catalog);
            }

            yield break;
        }

        if (context.ActorType == typeof(ServiceServingSetManagerGAgent))
        {
            if (payload.Is(ServiceServingSetUpdatedEvent.Descriptor))
            {
                yield return DurablePlan<ServiceServingSetProjectionContext>(
                    context.ActorId,
                    ServiceProjectionKinds.Serving);
                yield return DurablePlan<ServiceTrafficViewProjectionContext>(
                    context.ActorId,
                    ServiceProjectionKinds.Traffic);
            }

            yield break;
        }

        if (context.ActorType == typeof(ServiceRolloutManagerGAgent))
        {
            yield return DurablePlan<ServiceRolloutProjectionContext>(
                context.ActorId,
                ServiceProjectionKinds.Rollouts);
            yield break;
        }

        if (context.ActorType == typeof(ServiceRunGAgent))
        {
            yield return DurablePlan<ServiceRunCurrentStateProjectionContext>(
                context.ActorId,
                ServiceProjectionKinds.Runs);
            yield break;
        }

        if (context.ActorType == typeof(LlmSessionGAgent))
        {
            yield return DurablePlan<LlmSessionCurrentStateProjectionContext>(
                context.ActorId,
                ServiceProjectionKinds.ResponseSessions);
            yield break;
        }

        if (context.ActorType == typeof(ResponsesAgentToolStateGAgent))
        {
            yield return DurablePlan<ResponsesAgentToolStateCurrentStateProjectionContext>(
                context.ActorId,
                ServiceProjectionKinds.ResponsesAgentTools);
        }
    }

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
