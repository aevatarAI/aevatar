using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Schedules;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class TeamAutomationOperationObservationRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<TeamAutomationOperationCommittedOutcome>,
      ITeamAutomationOperationObservationProjectionLease,
      IProjectionContextRuntimeLease<TeamAutomationOperationObservationProjectionContext>
{
    public TeamAutomationOperationObservationRuntimeLease(
        TeamAutomationOperationObservationProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        OperationId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string OperationId { get; }

    public TeamAutomationOperationObservationProjectionContext Context { get; }
}
