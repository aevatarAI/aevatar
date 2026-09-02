using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class NyxIdAuthorizationCatalogRefreshObservationRuntimeLease
    : EventSinkProjectionRuntimeLeaseBase<NyxIdAuthorizationCatalogRefreshCommittedOutcome>,
      INyxIdAuthorizationCatalogRefreshObservationProjectionLease,
      IProjectionContextRuntimeLease<NyxIdAuthorizationCatalogRefreshObservationProjectionContext>
{
    public NyxIdAuthorizationCatalogRefreshObservationRuntimeLease(
        NyxIdAuthorizationCatalogRefreshObservationProjectionContext context)
        : base(context?.RootActorId ?? throw new ArgumentNullException(nameof(context)))
    {
        Context = context;
        RefreshId = context.SessionId;
    }

    public string ActorId => RootEntityId;

    public string RefreshId { get; }

    public NyxIdAuthorizationCatalogRefreshObservationProjectionContext Context { get; }
}
