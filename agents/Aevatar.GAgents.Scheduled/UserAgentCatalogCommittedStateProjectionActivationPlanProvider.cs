using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Scheduled;

// Refactor (iter52/issue-895-provider-coverage-contract):
//   Old pattern: New current-state readmodels added ad-hoc without enforced activation provider coverage; provider creation was a convention only.
//   New principle: CI guard requires every new current-state readmodel to have an associated IProjectionActivationPlanProvider implementation + DI + test, or an explicit [ProjectionExempt] classification.
public sealed class UserAgentCatalogCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.Published.StateEvent?.EventData;
        if (payload == null)
            return [];

        return context.ActorType switch
        {
            var type when type == typeof(UserAgentCatalogGAgent) &&
                          IsUserAgentCatalogEvent(payload) =>
            [
                DurablePlan(UserAgentCatalogGAgent.WellKnownId),
            ],
            _ => [],
        };
    }

    private static bool IsUserAgentCatalogEvent(Any payload) =>
        payload.Is(UserAgentCatalogUpsertedEvent.Descriptor) ||
        payload.Is(UserAgentCatalogTombstonedEvent.Descriptor) ||
        payload.Is(UserAgentCatalogTombstonesCompactedEvent.Descriptor);

    private static ProjectionActivationPlan DurablePlan(string rootActorId) =>
        new()
        {
            LeaseType = typeof(UserAgentCatalogMaterializationRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = rootActorId,
                ProjectionKind = UserAgentCatalogProjectionBootstrapActivator.ProjectionKind,
                Mode = ProjectionRuntimeMode.DurableMaterialization,
            },
        };
}
