using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Maps NyxID chat committed state events to per-turn observation scopes.
/// Session activation stays projection-owned: whenever the chat actor commits a
/// turn-bearing fact, the matching session-observation scope is (re)ensured so
/// attach-existing observers can bind without request-path priming.
/// </summary>
public sealed class NyxIdChatCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorType != typeof(NyxIdChatConversationGAgent))
            yield break;

        var eventData = context.Published.StateEvent?.EventData;
        if (eventData == null)
            yield break;

        var turnId = TryResolveTurnId(eventData);
        if (string.IsNullOrWhiteSpace(turnId))
            yield break;

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(NyxIdChatSessionRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = NyxIdChatProjectionKinds.ChatSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = turnId.Trim(),
            },
        };
    }

    private static string? TryResolveTurnId(Any eventData)
    {
        if (eventData.Is(NyxIdChatTurnStartedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatTurnStartedEvent>().State?.ActiveTurn?.TurnId;

        if (eventData.Is(NyxIdChatOperationDispatchedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatOperationDispatchedEvent>().Key?.TurnId;

        if (eventData.Is(NyxIdChatOperationProgressedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatOperationProgressedEvent>().Progress?.Key?.TurnId;

        if (eventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            return eventData.Unpack<NyxIdChatOperationReconciledEvent>().Result?.Key?.TurnId;

        if (eventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatControlFenceCommittedEvent>().Fence?.TurnId;

        if (eventData.Is(NyxIdChatActionRequestedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatActionRequestedEvent>().Request?.OriginTurnId;

        return null;
    }
}
