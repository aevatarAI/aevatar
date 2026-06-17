using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Aevatar.GAgents.Channel.Abstractions;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.Channel.Runtime;

public sealed class ConversationDeliveryCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public const string ProjectionKind = "conversation-delivery";

    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var payload = context.Published.StateEvent?.EventData;
        if (context.ActorType != typeof(ConversationGAgent) ||
            payload == null ||
            !IsConversationDeliveryEvent(payload))
        {
            return [];
        }

        return
        [
            new ProjectionActivationPlan
            {
                LeaseType = typeof(ConversationDeliveryMaterializationRuntimeLease),
                StartRequest = new ProjectionScopeStartRequest
                {
                    RootActorId = context.ActorId,
                    ProjectionKind = ProjectionKind,
                    Mode = ProjectionRuntimeMode.DurableMaterialization,
                },
            },
        ];
    }

    private static bool IsConversationDeliveryEvent(Any payload) =>
        payload.Is(DeliveryProducedEvent.Descriptor) ||
        payload.Is(LlmReplyDeliveredEvent.Descriptor) ||
        payload.Is(LlmReplyDeliveryFailedEvent.Descriptor) ||
        payload.Is(ConversationTurnCompletedEvent.Descriptor);
}
