using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.AI.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgentService.Abstractions;
using Aevatar.GAgentService.Core.GAgents;
using Aevatar.GAgentService.Core.Schedules;
using Aevatar.GAgentService.Core.Schedules.Authorization;
using Aevatar.GAgentService.Core.AgentProfiles;
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
            var type when type == typeof(ServiceInvocationCatalogGAgent) => InvocationCatalogPlans(context.ActorId),
            var type when type == typeof(ServiceRunGAgent) => ServiceRunPlans(context.ActorId),
            _ when payload.Is(RoleChatSessionCompletedEvent.Descriptor) => GAgentRunTerminalPlans(context),
            var type when type == typeof(LlmSessionGAgent) => LlmSessionPlans(context.ActorId),
            var type when type == typeof(ResponsesAgentToolStateGAgent) => ResponsesAgentToolPlans(context.ActorId),
            var type when type == typeof(ScheduledDispatchGAgent) => ScheduledDispatchPlans(context.ActorId),
            var type when type == typeof(NyxIdAuthorizationCatalogGAgent) => NyxIdAuthorizationCatalogPlans(context.ActorId),
            var type when type == typeof(AgentProfileNamespaceGAgent) => AgentProfileCatalogPlans(context.ActorId),
            var type when type == typeof(AgentProfileGAgent) => AgentProfileCurrentStatePlans(context.ActorId),
            _ => [],
        };
    }

    private static IEnumerable<ProjectionActivationPlan> DefinitionPlans(string actorId, Any payload)
    {
        if (!payload.Is(ServiceDefinitionCreatedEvent.Descriptor) &&
            !payload.Is(ServiceDefinitionUpdatedEvent.Descriptor) &&
            !payload.Is(ServiceRegistrationRequestedEvent.Descriptor) &&
            !payload.Is(ServiceRegistrationAttemptStartedEvent.Descriptor) &&
            !payload.Is(ServiceRegistrationSucceededEvent.Descriptor) &&
            !payload.Is(ServiceRegistrationFailedEvent.Descriptor) &&
            !payload.Is(ServiceRegistrationRetiredEvent.Descriptor) &&
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

    private static IEnumerable<ProjectionActivationPlan> ScheduledDispatchPlans(string actorId) =>
    [
        DurablePlan<ScheduledDispatchProjectionContext>(
            actorId,
            ServiceProjectionKinds.ScheduledDispatches),
    ];

    private static IEnumerable<ProjectionActivationPlan> NyxIdAuthorizationCatalogPlans(string actorId) =>
    [
        DurablePlan<NyxIdAuthorizationCatalogProjectionContext>(
            actorId,
            ServiceProjectionKinds.NyxIdAuthorizationCatalog),
    ];

    private static IEnumerable<ProjectionActivationPlan> AgentProfileCatalogPlans(string actorId) =>
    [
        DurablePlan<AgentProfileCatalogProjectionContext>(
            actorId,
            ServiceProjectionKinds.AgentProfileCatalog),
    ];

    private static IEnumerable<ProjectionActivationPlan> AgentProfileCurrentStatePlans(string actorId) =>
    [
        DurablePlan<AgentProfileCurrentStateProjectionContext>(
            actorId,
            ServiceProjectionKinds.AgentProfileCurrentState),
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

    private static IEnumerable<ProjectionActivationPlan> InvocationCatalogPlans(string actorId) =>
    [
        DurablePlan<ServiceInvocationCatalogProjectionContext>(
            actorId,
            ServiceProjectionKinds.InvocationCatalog),
    ];

    private static IEnumerable<ProjectionActivationPlan> ServiceRunPlans(string actorId) =>
    [
        DurablePlan<ServiceRunCurrentStateProjectionContext>(
            actorId,
            ServiceProjectionKinds.Runs),
    ];

    private static IEnumerable<ProjectionActivationPlan> GAgentRunTerminalPlans(CommittedStatePublicationContext context)
    {
        var payload = context.Published.StateEvent?.EventData;
        if (payload?.Is(RoleChatSessionCompletedEvent.Descriptor) != true)
            return [];

        var correlationId = context.SourceEnvelope?.Propagation?.CorrelationId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(correlationId))
            return [];

        return
        [
            DurablePlan<GAgentRunTerminalProjectionContext>(
                context.ActorId,
                ResolveTerminalProjectionKind(payload.Unpack<RoleChatSessionCompletedEvent>()),
                correlationId),
        ];
    }

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
        string projectionKind,
        string sessionId = "")
        where TContext : class, IProjectionMaterializationContext =>
        new()
        {
            LeaseType = typeof(ServiceProjectionRuntimeLease<TContext>),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = actorId,
                ProjectionKind = projectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
                SessionId = sessionId,
            },
        };

    private static string ResolveTerminalProjectionKind(RoleChatSessionCompletedEvent completed)
    {
        if (IsApprovalTerminalCompletion(completed))
            return ServiceProjectionKinds.GAgentRunTerminalApproval;

        return ServiceProjectionKinds.GAgentRunTerminalDraftRun;
    }

    private static bool IsApprovalTerminalCompletion(RoleChatSessionCompletedEvent completed)
    {
        var content = completed.Content ?? string.Empty;
        return content.StartsWith("[[AEVATAR_LLM_ERROR]]", StringComparison.Ordinal) &&
               (content.Contains("approval_continuation_failed:", StringComparison.Ordinal) ||
                content.Contains("approval_denied:", StringComparison.Ordinal) ||
                content.Contains("approval_timeout:", StringComparison.Ordinal));
    }
}
