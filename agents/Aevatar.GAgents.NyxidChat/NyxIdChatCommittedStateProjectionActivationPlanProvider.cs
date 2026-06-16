using Aevatar.AI.Abstractions;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.Foundation.Abstractions.EventSourcing;
using Google.Protobuf.WellKnownTypes;

namespace Aevatar.GAgents.NyxidChat;

/// <summary>
/// Maps NyxID chat committed state events to chat-session observation scopes.
/// Session activation stays projection-owned: whenever the chat actor commits a
/// session-bearing fact, the matching session-observation scope is (re)ensured so
/// attach-existing observers can bind without request-path priming.
/// </summary>
public sealed class NyxIdChatCommittedStateProjectionActivationPlanProvider
    : IProjectionActivationPlanProvider
{
    public IEnumerable<ProjectionActivationPlan> GetPlans(CommittedStatePublicationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ActorType != typeof(NyxIdChatGAgent))
            yield break;

        var eventData = context.Published.StateEvent?.EventData;
        if (eventData == null)
            yield break;

        var sessionId = TryResolveSessionId(eventData);
        if (string.IsNullOrWhiteSpace(sessionId))
            yield break;

        yield return new ProjectionActivationPlan
        {
            LeaseType = typeof(NyxIdChatSessionRuntimeLease),
            StartRequest = new ProjectionScopeStartRequest
            {
                RootActorId = context.ActorId,
                ProjectionKind = NyxIdChatProjectionKinds.ChatSession,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = sessionId.Trim(),
            },
        };
    }

    private static string? TryResolveSessionId(Any eventData)
    {
        if (eventData.Is(RoleChatSessionStartedEvent.Descriptor))
            return eventData.Unpack<RoleChatSessionStartedEvent>().SessionId;

        if (eventData.Is(RoleChatSessionCompletedEvent.Descriptor))
            return eventData.Unpack<RoleChatSessionCompletedEvent>().SessionId;

        if (eventData.Is(PendingToolApprovalPersistedEvent.Descriptor))
            return eventData.Unpack<PendingToolApprovalPersistedEvent>().Pending?.SessionId;

        return null;
    }
}
