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
        if (eventData.Is(NyxIdChatOperationStepChangedCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatOperationStepChangedCommittedEvent>().Key?.TurnId;
        if (eventData.Is(NyxIdChatOperationStalledEvent.Descriptor))
            return eventData.Unpack<NyxIdChatOperationStalledEvent>().Key?.TurnId;

        if (eventData.Is(NyxIdChatOperationReconciledEvent.Descriptor))
            return eventData.Unpack<NyxIdChatOperationReconciledEvent>().Result?.Key?.TurnId;

        if (eventData.Is(NyxIdChatLateOperationEvidenceCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatLateOperationEvidenceCommittedEvent>().Key?.TurnId;

        if (eventData.Is(NyxIdChatControlFenceCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatControlFenceCommittedEvent>().Fence?.TurnId;

        if (eventData.Is(NyxIdChatActionRequestedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatActionRequestedEvent>().Request?.OriginTurnId;

        if (eventData.Is(NyxIdChatInputRequestedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatInputRequestedEvent>().PendingInput?.TurnId;

        if (eventData.Is(NyxIdChatInputResolutionCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatInputResolutionCommittedEvent>()
                .State?.ActiveTurn?.TurnId;

        if (eventData.Is(NyxIdChatApprovalResolutionCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatApprovalResolutionCommittedEvent>()
                .State?.ActiveTurn?.TurnId;

        if (eventData.Is(NyxIdChatContinuationAdmissionCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatContinuationAdmissionCommittedEvent>()
                .Admission?.OriginTurnId;

        if (eventData.Is(NyxIdChatPendingSteeringContinuationFinalizedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatPendingSteeringContinuationFinalizedEvent>()
                .State?.ContinuationAdmission?.OriginTurnId;

        if (eventData.Is(NyxIdChatStepControlCommittedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatStepControlCommittedEvent>().Result?.TurnId;

        if (eventData.Is(NyxIdChatTurnAdmissionRejectedEvent.Descriptor))
            return eventData.Unpack<NyxIdChatTurnAdmissionRejectedEvent>().RequestedTurnId;

        return null;
    }
}
