using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;

namespace Aevatar.GAgents.StatusDashboard;

/// <summary>
/// Maps committed health-probe state events to the durable current-state
/// projection scope owned by the projection pipeline.
/// </summary>
// Refactor (iter47/cluster-005-status-dashboard-startup-projection-activation):
//   Old pattern: Startup service explicitly ensures projection scopes and uses Task.Delay retry before dispatching configure commands.
//   New principle: Startup path dispatches actor configuration only; projection activation owned by committed-state hooks; retry uses hosted-service scheduling.
public sealed class HealthProbeCommittedStateProjectionActivationPlanProvider : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.ActorType != typeof(HealthProbeTargetGAgent) ||
            context.Published.StateEvent?.EventData == null ||
            (!context.Published.StateEvent.EventData.Is(HealthProbeConfigured.Descriptor) &&
             !context.Published.StateEvent.EventData.Is(HealthProbeObserved.Descriptor)))
        {
            yield break;
        }

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(HealthProbeMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = HealthProbeTargetGAgent.ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
    }
}
