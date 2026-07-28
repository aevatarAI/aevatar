using Aevatar.CQRS.Core.Abstractions.Streaming;
using Aevatar.CQRS.Projection.Core.Abstractions;
using Aevatar.CQRS.Projection.Core.Orchestration;
using Aevatar.GAgentService.Abstractions.Schedules.Authorization;
using Aevatar.GAgentService.Projection.Configuration;

namespace Aevatar.GAgentService.Projection.Orchestration;

public sealed class NyxIdAuthorizationCatalogRefreshObservationProjectionPort
    : EventSinkProjectionLifecyclePortBase<
        INyxIdAuthorizationCatalogRefreshObservationProjectionLease,
        NyxIdAuthorizationCatalogRefreshObservationRuntimeLease,
        NyxIdAuthorizationCatalogRefreshCommittedOutcome>,
      INyxIdAuthorizationCatalogRefreshObservationProjectionPort
{
    private readonly IProjectionScopeAttachExistingLeaseLookup<
        NyxIdAuthorizationCatalogRefreshObservationRuntimeLease> _attachExistingLeaseLookup;

    public NyxIdAuthorizationCatalogRefreshObservationProjectionPort(
        ServiceProjectionOptions options,
        IProjectionScopeReleaseService<NyxIdAuthorizationCatalogRefreshObservationRuntimeLease>
            releaseService,
        IProjectionSessionEventHub<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sessionEventHub,
        IProjectionScopeAttachExistingLeaseLookup<
            NyxIdAuthorizationCatalogRefreshObservationRuntimeLease> attachExistingLeaseLookup)
        : base(() => options.Enabled, releaseService, sessionEventHub)
    {
        _attachExistingLeaseLookup = attachExistingLeaseLookup ??
                                     throw new ArgumentNullException(nameof(attachExistingLeaseLookup));
    }

    public async Task<
        EventSinkProjectionAttachment<INyxIdAuthorizationCatalogRefreshObservationProjectionLease>?>
        AttachExistingRefreshProjectionAsync(
            string actorId,
            string refreshId,
            IEventSink<NyxIdAuthorizationCatalogRefreshCommittedOutcome> sink,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ct.ThrowIfCancellationRequested();

        if (!ProjectionEnabled ||
            string.IsNullOrWhiteSpace(actorId) ||
            string.IsNullOrWhiteSpace(refreshId))
        {
            return null;
        }

        var lease = await _attachExistingLeaseLookup.TryGetAsync(
            new ProjectionScopeStartRequest
            {
                RootActorId = actorId.Trim(),
                ProjectionKind = ServiceProjectionKinds.NyxIdAuthorizationCatalogRefreshObservation,
                Mode = ProjectionRuntimeMode.SessionObservation,
                SessionId = refreshId.Trim(),
            },
            ct).ConfigureAwait(false);
        if (lease == null)
            return null;

        var liveSinkLease = await AttachLiveSinkAsync(lease, sink, ct).ConfigureAwait(false);
        return liveSinkLease == null
            ? null
            : new EventSinkProjectionAttachment<
                INyxIdAuthorizationCatalogRefreshObservationProjectionLease>(lease, liveSinkLease);
    }
}
