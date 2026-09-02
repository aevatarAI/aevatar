using Aevatar.CQRS.Core.Abstractions.Streaming;

namespace Aevatar.GAgentService.Abstractions.Schedules.Authorization;

public enum NyxIdAuthorizationCatalogRefreshOutcomeStatus
{
    Started = 1,
    Observed = 2,
    Failed = 3,
    AccessDenied = 4,
    CatalogUnstable = 5,
    Superseded = 6,
}

public sealed record NyxIdAuthorizationCatalogRefreshCommittedOutcome(
    string RefreshId,
    NyxIdAuthorizationCatalogRefreshOutcomeStatus Status,
    long StateVersion,
    string FailureCode,
    DateTimeOffset ObservedAtUtc);

public sealed record NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation(
    string ActorId,
    string RefreshId);

public interface INyxIdAuthorizationCatalogRefreshObservationProjectionLease
{
    string ActorId { get; }

    string RefreshId { get; }
}

public interface INyxIdAuthorizationCatalogRefreshObservationProjectionPort
    : IEventSinkProjectionLifecyclePort<
        INyxIdAuthorizationCatalogRefreshObservationProjectionLease,
        NyxIdAuthorizationCatalogRefreshCommittedOutcome>
{
    Task<EventSinkProjectionAttachment<INyxIdAuthorizationCatalogRefreshObservationProjectionLease>?>
        AttachExistingRefreshProjectionAsync(
            string actorId,
            string refreshId,
            IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
            CancellationToken ct = default);
}

public interface INyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparationPort
{
    Task<NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation?> PrepareAsync(
        string actorId,
        string refreshId,
        CancellationToken ct = default);

    Task ReleaseAsync(
        NyxIdAuthorizationCatalogRefreshObservationScopeLeasePreparation preparation,
        CancellationToken ct = default);
}
