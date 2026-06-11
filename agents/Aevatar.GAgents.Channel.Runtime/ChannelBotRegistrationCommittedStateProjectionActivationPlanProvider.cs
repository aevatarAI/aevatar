using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

// Refactor (iter52/issue-895-provider-coverage-contract):
//   Old pattern: New current-state readmodels added ad-hoc without enforced activation provider coverage; provider creation was a convention only.
//   New principle: CI guard requires every new current-state readmodel to have an associated IProjectionActivationPlanProvider implementation + DI + test, or an explicit [ProjectionExempt] classification.
public sealed class ChannelBotRegistrationCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.Published.StateEvent?.EventData;
        if (context.ActorType != typeof(ChannelBotRegistrationGAgent) ||
            payload == null ||
            !IsChannelBotRegistrationEvent(payload))
        {
            return [];
        }

        return
        [
            new ProjectionActivationPlan
            {
                LeaseType = typeof(ChannelBotRegistrationMaterializationRuntimeLease),
                StartRequest = new ProjectionScopeStartRequest
                {
                    RootActorId = context.ActorId,
                    ProjectionKind = ChannelBotRegistrationProjectionBootstrapActivator.ProjectionKind,
                    Mode = ProjectionRuntimeMode.DurableMaterialization,
                },
            },
        ];
    }

    private static bool IsChannelBotRegistrationEvent(Any payload) =>
        payload.Is(ChannelBotRegisteredEvent.Descriptor) ||
        payload.Is(ChannelBotUnregisteredEvent.Descriptor) ||
        payload.Is(ChannelBotTombstonesCompactedEvent.Descriptor) ||
        payload.Is(ChannelBotRegistrationRejectedEvent.Descriptor) ||
        payload.Is(ChannelBotScopeIdRepairedEvent.Descriptor);
}
